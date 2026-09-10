using System;
using System.Collections.Concurrent;
using System.Globalization;
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
        [Serializable]
        private sealed class WellTelemetryJson
        {
            public string device;
            public float distance_cm;
            public float tds_ppm;
            public float ph;
        }

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

            public MainThreadEvent(MainThreadEventType type, string message = null)
            {
                Type = type;
                Message = message;
            }
        }

        private const float SensorTimeoutSeconds = 10f;
        private static readonly Color LiveColor = new(0.36f, 0.88f, 0.62f);
        private static readonly Color OfflineColor = new(0.95f, 0.43f, 0.38f);
        private static readonly Color ConnectingColor = new(0.86f, 0.70f, 0.35f);

        private readonly ConcurrentQueue<MainThreadEvent> mainThreadEvents = new();
        private CancellationTokenSource cancellation;
        private ClientWebSocket socket;
        private WellVisualizer wellVisualizer;
        private TMP_Text connectionStatusText;
        private AquaTwinMqttSettingsData settings;
        private float lastValidMessageTime = float.NegativeInfinity;
        private bool hasReceivedMessage;
        private bool connectionOpen;

        public static bool LiveModeRequested { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ReadModeBeforeSceneLoads()
        {
            LiveModeRequested = AquaTwinMqttLocalSettings.Load().mode ==
                AquaTwinDataMode.LiveMqtt;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstall()
        {
            if (!LiveModeRequested)
                return;

            WellVisualizer visualizer = FindFirstObjectByType<WellVisualizer>();
            if (visualizer != null &&
                visualizer.GetComponent<HiveMqttWellDataSource>() == null)
            {
                visualizer.gameObject.AddComponent<HiveMqttWellDataSource>();
            }
        }

        private async void Start()
        {
            wellVisualizer = GetComponent<WellVisualizer>();
            settings = AquaTwinMqttLocalSettings.Load();
            FindExistingConnectionStatus();
            SetConnectionStatus("CONNECTING", ConnectingColor);

            if (string.IsNullOrWhiteSpace(settings.password))
            {
                Debug.LogError("[MQTT] Password missing. Open AquaTwin > MQTT Connection Setup.");
                SetConnectionStatus("OFFLINE", OfflineColor);
                return;
            }

            cancellation = new CancellationTokenSource();
            Debug.Log("[MQTT] Connecting...");
            await ConnectSubscribeAndReceiveAsync(cancellation.Token);
        }

        private void Update()
        {
            while (mainThreadEvents.TryDequeue(out MainThreadEvent queuedEvent))
                HandleMainThreadEvent(queuedEvent);

            if (connectionOpen && hasReceivedMessage &&
                Time.unscaledTime - lastValidMessageTime > SensorTimeoutSeconds)
            {
                hasReceivedMessage = false;
                Debug.LogWarning("[MQTT] Sensor timeout; no message received for 10 seconds.");
                SetConnectionStatus("OFFLINE", OfflineColor);
            }
        }

        private async void OnDestroy()
        {
            cancellation?.Cancel();
            if (socket == null)
                return;

            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                        "Unity Play Mode stopped", CancellationToken.None);
                }
            }
            catch
            {
                // Play Mode is already shutting down; no further action is required.
            }
            finally
            {
                socket.Dispose();
                socket = null;
            }
        }

        private async Task ConnectSubscribeAndReceiveAsync(CancellationToken token)
        {
            try
            {
                socket = new ClientWebSocket();
                socket.Options.AddSubProtocol("mqtt");

                Uri endpoint = new($"wss://{AquaTwinMqttLocalSettings.BrokerHost}:" +
                    $"{AquaTwinMqttLocalSettings.BrokerPort}{AquaTwinMqttLocalSettings.WebSocketPath}");
                await socket.ConnectAsync(endpoint, token);
                await SendPacketAsync(BuildConnectPacket(settings.username, settings.password), token);

                byte[] connAck = await ReceiveWebSocketMessageAsync(token);
                ValidateConnAck(connAck);
                connectionOpen = true;
                mainThreadEvents.Enqueue(new MainThreadEvent(MainThreadEventType.Connected));

                await SendPacketAsync(BuildSubscribePacket(AquaTwinMqttLocalSettings.Topic), token);
                byte[] subAck = await ReceiveWebSocketMessageAsync(token);
                ValidateSubAck(subAck);
                mainThreadEvents.Enqueue(new MainThreadEvent(MainThreadEventType.Subscribed));

                _ = PingLoopAsync(token);
                await ReceiveLoopAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Expected when leaving Play Mode.
            }
            catch (Exception exception)
            {
                mainThreadEvents.Enqueue(new MainThreadEvent(
                    MainThreadEventType.Error, "[MQTT] " + exception.Message));
            }
            finally
            {
                connectionOpen = false;
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
            }
        }

        private async Task SendPacketAsync(byte[] packet, CancellationToken token)
        {
            await socket.SendAsync(new ArraySegment<byte>(packet),
                WebSocketMessageType.Binary, true, token);
        }

        private async Task<byte[]> ReceiveWebSocketMessageAsync(CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            using MemoryStream stream = new();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new IOException("Broker closed the WebSocket connection.");
                if (result.MessageType != WebSocketMessageType.Binary)
                    continue;
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
            return stream.ToArray();
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

            if (topic != AquaTwinMqttLocalSettings.Topic)
                return;

            string payload = Encoding.UTF8.GetString(bytes, offset, end - offset);
            mainThreadEvents.Enqueue(new MainThreadEvent(MainThreadEventType.Payload, payload));
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
                    SetConnectionStatus("OFFLINE", OfflineColor);
                    break;
                case MainThreadEventType.Connected:
                    Debug.Log("[MQTT] Connected");
                    break;
                case MainThreadEventType.Subscribed:
                    Debug.Log("[MQTT] Subscribed:\n" + AquaTwinMqttLocalSettings.Topic);
                    break;
                case MainThreadEventType.Payload:
                    ProcessTelemetryJson(queuedEvent.Message);
                    break;
                case MainThreadEventType.Disconnected:
                    SetConnectionStatus("OFFLINE", OfflineColor);
                    break;
            }
        }

        private void ProcessTelemetryJson(string json)
        {
            Debug.Log("[MQTT] Message received");
            try
            {
                if (!ContainsRequiredFields(json))
                    throw new FormatException("JSON is missing device, distance_cm, tds_ppm, or ph.");

                WellTelemetryJson telemetry = JsonUtility.FromJson<WellTelemetryJson>(json);
                if (telemetry == null || string.IsNullOrWhiteSpace(telemetry.device) ||
                    !IsFinite(telemetry.distance_cm) || !IsFinite(telemetry.tds_ppm) ||
                    !IsFinite(telemetry.ph))
                {
                    throw new FormatException("JSON contains invalid sensor values.");
                }

                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "distance_cm = {0:F2}\ntds_ppm = {1:F2}\nph = {2:F2}",
                    telemetry.distance_cm, telemetry.tds_ppm, telemetry.ph));

                lastValidMessageTime = Time.unscaledTime;
                hasReceivedMessage = true;
                SetConnectionStatus("LIVE", LiveColor);

                if (settings.applyToDigitalTwin && wellVisualizer != null)
                {
                    wellVisualizer.SetLiveSensorData(
                        telemetry.distance_cm, telemetry.tds_ppm, telemetry.ph);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[MQTT] Ignored malformed message: " + exception.Message);
            }
        }

        private static bool ContainsRequiredFields(string json)
        {
            return !string.IsNullOrWhiteSpace(json) &&
                json.Contains("\"device\"") &&
                json.Contains("\"distance_cm\"") &&
                json.Contains("\"tds_ppm\"") &&
                json.Contains("\"ph\"");
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

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

        private void SetConnectionStatus(string state, Color color)
        {
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

        private static byte[] BuildSubscribePacket(string topic)
        {
            using MemoryStream body = new();
            body.WriteByte(0x00);
            body.WriteByte(0x01);
            WriteMqttString(body, topic);
            body.WriteByte(0x00);
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
            if (packet.Length < 5 || (packet[0] >> 4) != 9 || packet[^1] == 0x80)
                throw new InvalidDataException("Broker rejected the topic subscription.");
        }
    }
}
