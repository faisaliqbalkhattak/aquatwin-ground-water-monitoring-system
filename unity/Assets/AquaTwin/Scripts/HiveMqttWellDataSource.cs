using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace AquaTwin
{
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class HiveMqttWellDataSource : MonoBehaviour
    {
        private enum MainThreadEventType
        {
            Log,
            Warning,
            Error,
            Connected,
            Subscribed,
            Payload,
            Disconnected
        }

        private readonly struct MainThreadEvent
        {
            public readonly MainThreadEventType Type;
            public readonly string Message;
            public readonly string Topic;
            public readonly bool Retained;

            public MainThreadEvent(MainThreadEventType type, string message = null, string topic = null, bool retained = false)
            {
                Type = type;
                Message = message;
                Topic = topic;
                Retained = retained;
            }
        }

        private static readonly Color LiveColor = new(0.36f, 0.88f, 0.62f);
        private static readonly Color OfflineColor = new(0.95f, 0.43f, 0.38f);
        private static readonly Color ConnectingColor = new(0.86f, 0.70f, 0.35f);

        private readonly ConcurrentQueue<MainThreadEvent> mainThreadEvents = new();
        private CancellationTokenSource cancellation;
        private ClientWebSocket socket;
        private FleetDashboard dashboard;
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly List<byte> receiveBuffer = new();
        private TMP_Text connectionStatusText;
        private AquaTwinMqttSettingsData settings;
        private Task connectionTask;
        private bool restarting, shuttingDown;
        private bool CredentialsMissing => string.IsNullOrWhiteSpace(settings?.password) || string.IsNullOrWhiteSpace(settings?.username);
        private string OfflineDetail => CredentialsMissing
            ? "HiveMQ is not authenticated. Open Settings to add credentials, or use Test demo."
            : "Connection failed. Check credentials/network, retry, or use Test demo.";

        public static bool LiveModeRequested { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ReadModeBeforeSceneLoads()
        {
            // Always check the broker first. Demo playback requires an explicit UI action.
            LiveModeRequested = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstall()
        {
            FleetDashboard fleet = FleetDashboard.EnsureInstance();
            if (fleet.GetComponent<HiveMqttWellDataSource>() == null)
                fleet.gameObject.AddComponent<HiveMqttWellDataSource>();
        }

        private void Start() { RetryConnection(); }
        public void ApplySettings(AquaTwinMqttSettingsData updated, bool reconnect)
        {
            if (reconnect) RetryConnection();
            else settings = updated.Copy();
        }

        public async void RetryConnection()
        {
            if (restarting || shuttingDown) return;
            restarting = true;
            try
            {
                cancellation?.Cancel();
                socket?.Abort();
                if (connectionTask != null) await connectionTask;
                cancellation?.Dispose();
                cancellation = null;
                if (shuttingDown) return;
                while (mainThreadEvents.TryDequeue(out _)) { }
                dashboard = FleetDashboard.EnsureInstance();
                settings = AquaTwinMqttLocalSettings.Load();
                FindExistingConnectionStatus();
                SetConnectionStatus("CONNECTING", ConnectingColor, "Opening secure HiveMQ connection…");
                cancellation = new CancellationTokenSource();
                connectionTask = RunConnectionLoopAsync(cancellation.Token);
            }
            catch (Exception exception)
            {
                if (!shuttingDown) SetConnectionStatus("OFFLINE", OfflineColor, "Connection could not start. Retry HiveMQ or use Test demo.");
                Debug.LogWarning("[MQTT] Startup failed: " + exception.Message);
            }
            finally { restarting = false; }
        }

        private async Task RunConnectionLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await ConnectSubscribeAndReceiveAsync(token);
                // Probe the endpoint once without credentials, then wait for an explicit retry after setup.
                if (token.IsCancellationRequested || CredentialsMissing) break;
                try { await Task.Delay(5000, token); }
                catch (OperationCanceledException) { break; }
                mainThreadEvents.Enqueue(new MainThreadEvent(MainThreadEventType.Log, "[MQTT] Retrying connection..."));
            }
        }

        private void Update()
        {
            for (int i = 0; i < 256 && mainThreadEvents.TryDequeue(out MainThreadEvent queuedEvent); i++)
                HandleMainThreadEvent(queuedEvent);

        }

        private void OnDestroy()
        {
            shuttingDown = true;
            cancellation?.Cancel();
            socket?.Abort();
        }

        private async Task ConnectSubscribeAndReceiveAsync(CancellationToken token)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task pingTask = null;
            token = session.Token;
            try
            {
                receiveBuffer.Clear();
                socket = new ClientWebSocket();
                socket.Options.AddSubProtocol("mqtt");

                Uri endpoint = settings.Endpoint;
                // Bound the complete WebSocket + MQTT handshake, including authentication/subscription.
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));
                    await socket.ConnectAsync(endpoint, handshake.Token);
                    if (CredentialsMissing) throw new InvalidOperationException("MQTT credentials are missing. Open dashboard Settings.");
                    await SendPacketAsync(BuildConnectPacket(settings.username, settings.password), handshake.Token);
                    byte[] connAck = await ReceiveWebSocketMessageAsync(handshake.Token);
                    ValidateConnAck(connAck);
                    await SendPacketAsync(BuildSubscribePacket(AquaTwinMqttLocalSettings.LegacyFleetTopic, AquaTwinMqttLocalSettings.FleetTopic), handshake.Token);
                    byte[] subAck = await ReceiveWebSocketMessageAsync(handshake.Token);
                    ValidateSubAck(subAck);
                }
                mainThreadEvents.Enqueue(new MainThreadEvent(MainThreadEventType.Subscribed));

                pingTask = PingLoopAsync(token);
                await ReceiveLoopAsync(token);
            }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested)
                    mainThreadEvents.Enqueue(new MainThreadEvent(MainThreadEventType.Warning, "[MQTT] Connection or receive timed out."));
            }
            catch (Exception exception)
            {
                mainThreadEvents.Enqueue(new MainThreadEvent(
                    MainThreadEventType.Error, "[MQTT] " + exception.Message));
            }
            finally
            {
                session.Cancel();
                if (pingTask != null) await pingTask;
                socket?.Dispose();
                socket = null;
                mainThreadEvents.Enqueue(new MainThreadEvent(MainThreadEventType.Disconnected));
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                byte[] message = await ReceiveWebSocketMessageAsync(token);
                ParseMqttPackets(message);
            }
        }

        private async Task PingLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), token);
                    await SendPacketAsync(new byte[] { 0xC0, 0x00 }, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                mainThreadEvents.Enqueue(new MainThreadEvent(
                    MainThreadEventType.Warning, "[MQTT] Keep-alive failed: " + exception.Message));
                socket?.Abort();
            }
        }

        private async Task SendPacketAsync(byte[] packet, CancellationToken token)
        {
            await sendLock.WaitAsync(token);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, token);
            }
            finally { sendLock.Release(); }
        }

        private async Task<byte[]> ReceiveWebSocketMessageAsync(CancellationToken token)
        {
            // MQTT packets can be split across WebSocket frames or coalesced in one frame.
            byte[] buffer = new byte[8192];
            while (true)
            {
                if (receiveBuffer.Count >= 2)
                {
                    byte[] pending = receiveBuffer.ToArray();
                    int offset = 1;
                    int length = DecodeRemainingLength(pending, ref offset);
                    if (length >= 0 && pending.Length >= offset + length)
                    {
                        byte[] packet = receiveBuffer.GetRange(0, offset + length).ToArray();
                        receiveBuffer.RemoveRange(0, offset + length);
                        return packet;
                    }
                    if (length < 0 && pending.Length >= 5)
                        throw new InvalidDataException("Malformed MQTT remaining length.");
                    if (length > 1024 * 1024) throw new InvalidDataException("MQTT message exceeds 1 MB.");
                }
                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                receiveTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), receiveTimeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new IOException("Broker closed the WebSocket connection.");
                if (result.MessageType != WebSocketMessageType.Binary)
                    throw new InvalidDataException("MQTT requires binary WebSocket frames.");
                for (int i = 0; i < result.Count; i++) receiveBuffer.Add(buffer[i]);
                if (receiveBuffer.Count > 1024 * 1024 + 8192)
                    throw new InvalidDataException("MQTT receive buffer exceeds limit.");
            }
        }

        private void ParseMqttPackets(byte[] bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                byte header = bytes[offset++];
                int remainingLength = DecodeRemainingLength(bytes, ref offset);
                if (remainingLength < 0 || offset + remainingLength > bytes.Length)
                    throw new InvalidDataException("Malformed MQTT packet length.");

                int packetType = header >> 4;
                if (packetType == 3)
                    ParsePublishPacket(header, bytes, offset, remainingLength);
                offset += remainingLength;
            }
        }

        private void ParsePublishPacket(byte header, byte[] bytes, int offset, int length)
        {
            int end = offset + length;
            if (offset + 2 > end)
                throw new InvalidDataException("Malformed MQTT PUBLISH packet.");

            int topicLength = (bytes[offset] << 8) | bytes[offset + 1];
            offset += 2;
            if (offset + topicLength > end)
                throw new InvalidDataException("Malformed MQTT topic.");

            string topic = Encoding.UTF8.GetString(bytes, offset, topicLength);
            offset += topicLength;
            int qos = (header >> 1) & 0x03;
            if (qos > 0)
                offset += 2;
            if (offset > end)
                throw new InvalidDataException("Malformed MQTT packet identifier.");

            if (!IsTelemetryTopic(topic)) return;
            if (qos != 0) throw new InvalidDataException("Expected QoS 0 on the subscriber connection.");

            string payload = Encoding.UTF8.GetString(bytes, offset, end - offset);
            if (mainThreadEvents.Count < 4096)
                mainThreadEvents.Enqueue(new MainThreadEvent(MainThreadEventType.Payload, payload, topic, (header & 1) != 0));
        }

        private void HandleMainThreadEvent(MainThreadEvent queuedEvent)
        {
            switch (queuedEvent.Type)
            {
                case MainThreadEventType.Log:
                    Debug.Log(queuedEvent.Message);
                    break;
                case MainThreadEventType.Warning:
                    Debug.LogWarning(queuedEvent.Message);
                    break;
                case MainThreadEventType.Error:
                    Debug.LogError(queuedEvent.Message);
                    SetConnectionStatus("OFFLINE", OfflineColor, OfflineDetail);
                    break;
                case MainThreadEventType.Connected:
                    Debug.Log("[MQTT] Connected");
                    break;
                case MainThreadEventType.Subscribed:
                    Debug.Log("[MQTT] Subscribed:\n" + AquaTwinMqttLocalSettings.LegacyFleetTopic + "\n" + AquaTwinMqttLocalSettings.FleetTopic);
                    SetConnectionStatus("CONNECTED", LiveColor, "Authenticated and subscribed · waiting for sensor readings");
                    break;
                case MainThreadEventType.Payload:
                    ProcessTelemetryJson(queuedEvent.Message, queuedEvent.Topic, queuedEvent.Retained);
                    break;
                case MainThreadEventType.Disconnected:
                    SetConnectionStatus("OFFLINE", OfflineColor, OfflineDetail);
                    break;
            }
        }

        private void ProcessTelemetryJson(string json, string topic, bool retained)
        {
            if (!settings.applyToDigitalTwin) return;
            if (!dashboard.ApplyTelemetry(json, topic, retained, out string error))
            {
                Debug.LogWarning("[MQTT] Ignored telemetry: " + error);
                return;
            }
            SetConnectionStatus("CONNECTED", LiveColor, "Authenticated and subscribed · receiving well telemetry");
        }

        private static bool IsTelemetryTopic(string topic)
        {
            string[] parts = topic.Split('/');
            return (parts.Length == 3 && parts[0] == "digitaltwin" && parts[2] == "sensors") ||
                (parts.Length == 4 && parts[0] == "aquatwin" && parts[1] == "well" && parts[3] == "telemetry");
        }

        private void FindExistingConnectionStatus()
        {
            TMP_Text[] labels = FindObjectsByType<TMP_Text>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (TMP_Text label in labels)
            {
                if (label.name == "Connection")
                {
                    connectionStatusText = label;
                    break;
                }
            }
        }

        private void SetConnectionStatus(string state, Color color, string detail = null)
        {
            if (dashboard != null) dashboard.SetTransportState(state, detail);
            if (connectionStatusText == null)
                FindExistingConnectionStatus();
            if (connectionStatusText == null)
                return;

            connectionStatusText.text = "●   " + state;
            connectionStatusText.color = color;
        }

        private static byte[] BuildConnectPacket(string username, string password)
        {
            using MemoryStream body = new();
            WriteMqttString(body, "MQTT");
            body.WriteByte(0x04);
            body.WriteByte(0xC2);
            body.WriteByte(0x00);
            body.WriteByte(0x1E);
            WriteMqttString(body, "AquaTwinUnity-" + Guid.NewGuid().ToString("N")[..12]);
            WriteMqttString(body, username);
            WriteMqttString(body, password);
            return AddFixedHeader(0x10, body.ToArray());
        }

        private static byte[] BuildSubscribePacket(params string[] topics)
        {
            using MemoryStream body = new();
            body.WriteByte(0x00);
            body.WriteByte(0x01);
            foreach (string topic in topics)
            {
                WriteMqttString(body, topic);
                body.WriteByte(0x00);
            }
            return AddFixedHeader(0x82, body.ToArray());
        }

        private static byte[] AddFixedHeader(byte header, byte[] body)
        {
            using MemoryStream packet = new();
            packet.WriteByte(header);
            WriteRemainingLength(packet, body.Length);
            packet.Write(body, 0, body.Length);
            return packet.ToArray();
        }

        private static void WriteMqttString(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            stream.WriteByte((byte)(bytes.Length >> 8));
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteRemainingLength(Stream stream, int length)
        {
            do
            {
                int encodedByte = length % 128;
                length /= 128;
                if (length > 0)
                    encodedByte |= 128;
                stream.WriteByte((byte)encodedByte);
            }
            while (length > 0);
        }

        private static int DecodeRemainingLength(byte[] bytes, ref int offset)
        {
            int multiplier = 1;
            int value = 0;
            byte encoded;
            do
            {
                if (offset >= bytes.Length || multiplier > 128 * 128 * 128)
                    return -1;
                encoded = bytes[offset++];
                value += (encoded & 127) * multiplier;
                multiplier *= 128;
            }
            while ((encoded & 128) != 0);
            return value;
        }

        private static void ValidateConnAck(byte[] packet)
        {
            if (packet.Length < 4 || (packet[0] >> 4) != 2)
                throw new InvalidDataException("Broker returned an invalid CONNACK packet.");
            if (packet[3] != 0)
                throw new UnauthorizedAccessException(
                    "Broker rejected the MQTT connection (CONNACK " + packet[3] + ").");
        }

        private static void ValidateSubAck(byte[] packet)
        {
            if (packet.Length != 6 || (packet[0] >> 4) != 9 || packet[4] != 0 || packet[5] != 0)
                throw new InvalidDataException("Broker rejected the topic subscription.");
        }
    }
}
