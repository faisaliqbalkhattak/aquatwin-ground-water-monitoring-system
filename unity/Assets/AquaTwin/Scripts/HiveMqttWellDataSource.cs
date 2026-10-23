using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AquaTwin
{
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class HiveMqttWellDataSource : MonoBehaviour
    {
        private MqttSocketTransport socket;
        private MqttProtocolSession protocol;
        private FleetDashboard dashboard;
        private AquaTwinMqttSettingsData settings;
        private readonly Queue<string> history = new();
        private string lastError = "", telemetryIssue = "", lastTopic = "—";
        private float retryAt = float.PositiveInfinity, lastMessageAt = -1, connectedAt;
        private float nextDiagnostics;
        private int attempts, received, accepted, rejected, receivedBeforeAttempt, rejectedBeforeAttempt;
        private bool shuttingDown, suspended;
        private readonly MqttRecoveryPolicy recovery = new();
        private FleetSnapshotCache snapshots;
        private string snapshotKey, snapshotNote = "";
        private bool snapshotDirty;
        private float nextSnapshotSave;
        public static bool LiveModeRequested => true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstall()
        {
            var fleet = FleetDashboard.EnsureInstance();
            if (fleet.GetComponent<HiveMqttWellDataSource>() == null)
                fleet.gameObject.AddComponent<HiveMqttWellDataSource>();
        }
        private void OnEnable()
        {
            shuttingDown = false;
            suspended = false;
            Application.runInBackground = true;
            RetryConnection();
        }
        private void OnDisable()
        {
            FlushSnapshots(); socket?.Dispose(); socket = null; protocol = null;
        }
        private void OnApplicationPause(bool paused)
        {
            if (paused) { suspended = true; FlushSnapshots(); socket?.Dispose(); socket = null; }
            else if (suspended && isActiveAndEnabled && !shuttingDown) { suspended = false; RetryConnection(); }
        }
        private void OnApplicationFocus(bool focused)
        {
            if (!focused) { FlushSnapshots(); return; }
            if (isActiveAndEnabled && !shuttingDown && settings != null &&
                (protocol == null || (protocol.State == "OFFLINE" && !double.IsPositiveInfinity(recovery.RetryAt))))
                RetryConnection();
        }
        public void ApplySettings(AquaTwinMqttSettingsData updated, bool reconnect)
        { if (reconnect) RetryConnection(); else settings = updated.Copy(); }
        public void RetryConnection()
        {
            if (shuttingDown || suspended) return;
            socket?.Dispose(); socket = null;
            settings = AquaTwinMqttLocalSettings.Load();
            dashboard = FleetDashboard.EnsureInstance();
            PrepareSnapshots();
            recovery.Starting(Time.realtimeSinceStartup);
            retryAt = float.PositiveInfinity;
            telemetryIssue = "";
            attempts++;
            receivedBeforeAttempt = received; rejectedBeforeAttempt = rejected;
            dashboard.SetTelemetryDelivery(0, 0);
            protocol = new MqttProtocolSession(settings.username, settings.password,
                settings.SubscriptionTopics, Time.realtimeSinceStartup);
            protocol.Status = OnStatus;
            protocol.Payload = ProcessTelemetryJson;
            protocol.Send = bytes => socket?.Send(bytes);
            try
            {
                if (!settings.TryValidate(out _, out string error)) { protocol.Fail(error); return; }
                if (string.IsNullOrWhiteSpace(settings.username) || string.IsNullOrEmpty(settings.password))
                { protocol.Fail("Credentials are missing. Open Settings and enter your MQTT username and password."); return; }
                OnStatus("CONNECTING", "Opening " + settings.Endpoint.AbsoluteUri + (settings.UsesDevicePort ? " · device port 8883 mapped to WSS 8884" : "") + " · attempt " + attempts);
                socket = new MqttSocketTransport(settings.Endpoint);
            }
            catch (Exception exception) { protocol.Fail("WebSocket could not start: " + Safe(exception.Message)); }
        }
        private void Update()
        {
            if (protocol == null || shuttingDown || suspended) return;
            float now = Time.realtimeSinceStartup;
            dashboard.SetTelemetryDelivery(received - receivedBeforeAttempt, rejected - rejectedBeforeAttempt);
            try
            {
                for (int i = 0; i < 256 && socket != null && socket.Poll(out var item); i++)
                {
                    if (protocol.State == "OFFLINE") break;
                    switch (item.Kind)
                    {
                        case "open": protocol.Open(now); break;
                        case "data": protocol.Feed(item.Data, now); break;
                        case "error": case "close": protocol.Fail(Safe(item.Message)); break;
                    }
                }
                protocol.Tick(now);
            }
            catch (Exception exception) { protocol.Fail("Connection processing failed: " + Safe(exception.Message)); }
            if (protocol.State == "OFFLINE")
            {
                socket?.Dispose(); socket = null;
                if (recovery.ShouldRetry(now)) { RetryConnection(); return; }
            }
            else if (protocol.State == "CONNECTED")
            {
                // A silent publisher is not a broken connection. The MQTT protocol
                // owns PINGREQ/PINGRESP timeouts; reconnect only after a transport failure.
                string detail = !settings.applyToDigitalTwin ? "MQTT connected · live twin updates are paused in Settings."
                    : !string.IsNullOrEmpty(telemetryIssue) ? telemetryIssue
                    : received > receivedBeforeAttempt ? "Receiving sensor messages · " + accepted + " accepted · last arrival " + Mathf.FloorToInt(now - lastMessageAt) + "s ago"
                    : now - connectedAt >= 15 ? "Broker connection healthy; no sensor publications received. Waiting without reconnecting. Check the running publisher, broker and topic."
                    : "Authenticated and subscribed · waiting for sensor messages";
                dashboard.SetTransportState("CONNECTED", detail);
            }
            if (snapshotDirty && now >= nextSnapshotSave) FlushSnapshots();
            if (now < nextDiagnostics) return;
            nextDiagnostics = now + .5f;
            string activity = protocol.State == "CONNECTED" && received == receivedBeforeAttempt
                ? "Broker authenticated and subscribed · no messages this connection (" + Mathf.FloorToInt(now - connectedAt) + "s). Ensure the publisher is running, uses this same broker and publishes to a subscribed topic."
                : protocol.Detail;
            string countdown = float.IsPositiveInfinity(retryAt) ? "Manual retry / Save & apply"
                : "Retry in " + Mathf.Max(0, Mathf.CeilToInt(retryAt - now)) + "s";
            dashboard.SetConnectionDiagnostics(settings.Endpoint.AbsoluteUri,
                string.Join(" · ", settings.SubscriptionTopics),
                "Attempt " + attempts + "   |   This connection " + (received - receivedBeforeAttempt) + " messages   |   Session received " + received + "   Accepted " + accepted + "   Rejected " + rejected +
                "   |   Keep-alive replies " + protocol.PingResponses + "   |   Last topic: " + lastTopic + (lastMessageAt < 0 ? "" : "   |   Last message " + Mathf.FloorToInt(now - lastMessageAt) + "s ago"),
                lastError, (protocol.State == "OFFLINE" ? countdown : activity) + (string.IsNullOrEmpty(snapshotNote) ? "" : "\n" + snapshotNote),
                string.Join("\n", history.ToArray()));
        }
        private void OnStatus(string state, string detail)
        {
            detail = Safe(detail);
            nextDiagnostics = 0;
            AddHistory(state + " · " + detail);
            if (state == "CONNECTED") { connectedAt = Time.realtimeSinceStartup; recovery.Connected(connectedAt); }
            if (state == "OFFLINE")
            {
                lastError = detail;
                recovery.Failed(detail, Time.realtimeSinceStartup);
                retryAt = (float)recovery.RetryAt;
                Debug.LogWarning("[AquaTwin MQTT] " + detail);
            }
            dashboard?.SetTransportState(state, detail);
        }
        private void ProcessTelemetryJson(string json, string topic, bool retained)
        {
            received++; lastMessageAt = Time.realtimeSinceStartup; lastTopic = topic;
            recovery.Received(lastMessageAt);
            if (!settings.applyToDigitalTwin) return;
            if (!dashboard.ApplyTelemetry(json, topic, retained, out string error))
            {
                rejected++; telemetryIssue = "Message rejected on " + topic + ": " + Safe(error);
                lastError = telemetryIssue; AddHistory(telemetryIssue); return;
            }
            accepted++;
            int previousCount = snapshots?.Count ?? 0;
            snapshots?.Remember(json, topic); snapshotDirty = true;
            // Persist discovery immediately, including multiple wells arriving in the same frame.
            if ((snapshots?.Count ?? 0) > previousCount || Time.realtimeSinceStartup >= nextSnapshotSave) FlushSnapshots();
            // Keep source freshness separate from broker connectivity, including retained snapshots.
            var fields = WellFleet.ReadJsonObject(json);
            double timestamp = fields.TryGetValue("timestamp", out var value) && value is double number ? number : double.NaN;
            double age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - timestamp;
            telemetryIssue = !double.IsNaN(age) && age > settings.staleAfterSeconds
                ? "Message accepted, but its sensor timestamp is " + Math.Floor(age) + "s old. Check the sensor clock; the well is shown as stale."
                : "";
            if (!string.IsNullOrEmpty(telemetryIssue)) lastError = telemetryIssue;
            else if (lastError.StartsWith("Message rejected") || lastError.StartsWith("Message accepted")) lastError = "";
        }
        private void PrepareSnapshots()
        {
            string scope = settings.Endpoint.AbsoluteUri + "|" + settings.username;
            if (snapshots != null && snapshots.Scope == scope) return;
            bool changedBroker = snapshots != null;
            FlushSnapshots(); snapshots = new FleetSnapshotCache(scope);
            snapshotKey = "AquaTwin.LastKnown.v1." + Hash128.Compute(scope);
            snapshotDirty = false;
            if (changedBroker) dashboard.ResetLiveSource();
            try
            {
                int restored = dashboard.RestoreLastKnown(snapshots, ReadSnapshot());
                snapshotNote = restored > 0 ? restored + " last-known well reading(s) restored. They become live only after a new broker message." : "No last-known readings saved yet.";
                AddHistory("CACHE · " + snapshotNote);
            }
            catch (Exception exception) { SnapshotFailure("restore", exception); }
        }
        private void FlushSnapshots()
        {
            if (!snapshotDirty || snapshots == null || string.IsNullOrEmpty(snapshotKey)) return;
            try
            {
                string json = snapshots.Encode();
                // Reserve space for settings within WebGL's browser PlayerPrefs quota.
                if (System.Text.Encoding.UTF8.GetByteCount(json) > 512 * 1024)
                { snapshotNote = "Live readings are available, but this fleet exceeds the local snapshot storage budget."; return; }
                WriteSnapshot(json); snapshotDirty = false;
                snapshotNote = snapshots.Count + " last-known well reading(s) saved for restart recovery.";
            }
            catch (Exception exception) { SnapshotFailure("save", exception); }
            finally { nextSnapshotSave = Time.realtimeSinceStartup + 5; }
        }
        private string SnapshotPath => Path.Combine(Path.GetDirectoryName(AquaTwinMqttLocalSettings.FilePath), snapshotKey + ".json");
        private string ReadSnapshot()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return PlayerPrefs.GetString(snapshotKey, "");
#else
            // Project/device file storage is independent of Unity Editor PlayerPrefs registration.
            if (File.Exists(SnapshotPath)) return File.ReadAllText(SnapshotPath);
            string legacy = PlayerPrefs.GetString(snapshotKey, "");
            if (!string.IsNullOrEmpty(legacy)) AquaTwinSettingsFileStore.WriteAtomic(SnapshotPath, legacy);
            return legacy;
#endif
        }
        private void WriteSnapshot(string json)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            PlayerPrefs.SetString(snapshotKey, json); PlayerPrefs.Save();
#else
            AquaTwinSettingsFileStore.WriteAtomic(SnapshotPath, json);
#endif
        }
        private void SnapshotFailure(string operation, Exception exception)
        {
            snapshotNote = "Last-known cache " + operation + " failed: " + Safe(exception.Message);
            AddHistory("CACHE ERROR · " + snapshotNote);
            Debug.LogWarning("[AquaTwin] " + snapshotNote);
        }
        private void OnApplicationQuit() { shuttingDown = true; FlushSnapshots(); }
        private string Safe(string text)
        {
            text ??= "Connection failed";
            if (!string.IsNullOrEmpty(settings?.password)) text = text.Replace(settings.password, "[redacted]");
            return text.Length > 700 ? text.Substring(0, 700) : text;
        }
        private void AddHistory(string text)
        {
            if (history.Count >= 12) history.Dequeue();
            history.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + Safe(text));
        }
        private void OnDestroy() { shuttingDown = true; FlushSnapshots(); socket?.Dispose(); socket = null; }
    }
}
