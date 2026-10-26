using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AquaTwin
{
    /// <summary>MQTT 3.1.1 framing and lifecycle, independent of Unity and browser APIs.</summary>
    public sealed class MqttProtocolSession
    {
        public string State { get; private set; } = "CONNECTING";
        public int PingResponses { get; private set; }
        public string Detail { get; private set; } = "Opening secure WebSocket";
        public Action<byte[]> Send;
        public Action<string, string> Status;
        public Action<string, string, bool> Payload;
        private readonly string username, password;
        private readonly string[] topics;
        private readonly HashSet<string> optionalTopics;
        public string SubscriptionWarnings { get; private set; } = "";
        private readonly List<byte> pending = new();
        private double deadline, lastPing, pingSent;
        private bool awaitingPing;
        private const int Limit = 1024 * 1024;

        public MqttProtocolSession(string username, string password, string[] topics, double now, string[] optionalTopics = null)
        {
            this.username = username; this.password = password; this.topics = topics;
            this.optionalTopics = new HashSet<string>(optionalTopics ?? Array.Empty<string>());
            deadline = now + 12;
        }
        public void Open(double now)
        {
            if (State != "CONNECTING") return;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            { Fail("Credentials are missing. Open Settings and enter your MQTT username and password."); return; }
            using var body = new MemoryStream();
            Text(body, "MQTT"); body.WriteByte(4); body.WriteByte(0xC2);
            body.WriteByte(0); body.WriteByte(60);
            Text(body, "AquaTwin-" + Guid.NewGuid().ToString("N").Substring(0, 16));
            Text(body, username); Text(body, password);
            Set("AUTHENTICATING", "WebSocket open · authenticating with HiveMQ");
            deadline = now + 12; Send?.Invoke(Packet(0x10, body.ToArray()));
        }
        public void Fail(string reason) { if (State != "OFFLINE") Set("OFFLINE", reason); }
        private void Set(string state, string detail) { State = state; Detail = detail; Status?.Invoke(state, detail); }
        public void Tick(double now)
        {
            if (State == "OFFLINE") return;
            if (State != "CONNECTED")
            {
                if (now >= deadline) Fail(State == "CONNECTING"
                    ? "WebSocket timed out. Check hostname, WSS port (HiveMQ Cloud normally 8884), /mqtt, TLS and your network."
                    : State == "AUTHENTICATING" ? "Timed out waiting for MQTT authentication (CONNACK)."
                    : "Timed out waiting for subscription acknowledgement (SUBACK). Check topic permissions.");
                return;
            }
            if (awaitingPing && now - pingSent >= 30) { Fail("HiveMQ keep-alive timed out; the connection was lost."); return; }
            if (!awaitingPing && now - lastPing >= 15)
            { lastPing = pingSent = now; awaitingPing = true; Send?.Invoke(new byte[] { 0xC0, 0 }); }
        }
        public void Feed(byte[] bytes, double now)
        {
            if (State == "OFFLINE") return;
            try
            {
                if (pending.Count + bytes.Length > Limit + 5) throw new InvalidDataException("MQTT receive buffer exceeds 1 MB.");
                pending.AddRange(bytes);
                while (pending.Count >= 2 && State != "OFFLINE")
                {
                    int cursor = 1, length = 0, multiplier = 1;
                    byte value;
                    do
                    {
                        if (cursor >= pending.Count) return;
                        value = pending[cursor++]; length += (value & 127) * multiplier; multiplier *= 128;
                        if (cursor == 5 && (value & 128) != 0) throw new InvalidDataException("Invalid MQTT packet length.");
                    } while ((value & 128) != 0);
                    if (length > Limit) throw new InvalidDataException("MQTT packet exceeds 1 MB.");
                    if (pending.Count < cursor + length) return;
                    byte header = pending[0]; byte[] body = pending.GetRange(cursor, length).ToArray();
                    pending.RemoveRange(0, cursor + length);
                    Handle(header, body, now);
                }
            }
            catch (Exception exception) { Fail("MQTT protocol error: " + exception.Message); }
        }
        private void Handle(byte header, byte[] body, double now)
        {
            int type = header >> 4;
            if (type == 2)
            {
                if (State != "AUTHENTICATING" || body.Length != 2 || body[0] != 0) throw new InvalidDataException("Invalid CONNACK.");
                if (body[1] != 0)
                {
                    string[] reasons = { "Accepted", "Unsupported MQTT version", "Client ID rejected", "Broker unavailable", "Incorrect username or password", "Not authorized: check credential permissions" };
                    Fail("MQTT authentication rejected (CONNACK " + body[1] + "): " + (body[1] < reasons.Length ? reasons[body[1]] : "Unknown broker rejection")); return;
                }
                using var subscription = new MemoryStream(); subscription.WriteByte(0); subscription.WriteByte(1);
                foreach (string topic in topics) { Text(subscription, topic); subscription.WriteByte(0); }
                Set("SUBSCRIBING", "Authenticated · requesting " + topics.Length + " topic subscription(s)");
                deadline = now + 12; Send?.Invoke(Packet(0x82, subscription.ToArray()));
            }
            else if (type == 9)
            {
                if (State != "SUBSCRIBING" || body.Length != topics.Length + 2 || body[0] != 0 || body[1] != 1)
                    throw new InvalidDataException("Invalid SUBACK.");
                for (int i = 0; i < topics.Length; i++)
                    if (body[i + 2] != 0)
                    {
                        if (optionalTopics.Contains(topics[i])) { SubscriptionWarnings += "Guardian subscription unavailable: " + topics[i] + ". "; continue; }
                        Fail("Subscription rejected for " + topics[i] + ". Check this credential's subscribe permissions (SUBACK " + body[i + 2] + ")."); return;
                    }
                lastPing = now;
                Set("CONNECTED", "Authenticated and subscribed · waiting for sensor messages");
            }
            else if (type == 3)
            {
                // Retained messages can arrive before SUBACK.
                if (State != "SUBSCRIBING" && State != "CONNECTED") throw new InvalidDataException("PUBLISH before authentication.");
                if (body.Length < 2) throw new InvalidDataException("Truncated PUBLISH.");
                int size = (body[0] << 8) | body[1], start = size + 2, qos = (header >> 1) & 3;
                if (size == 0 || start > body.Length || qos > 1) throw new InvalidDataException("Invalid PUBLISH topic or QoS.");
                string topic = Encoding.UTF8.GetString(body, 2, size);
                if (!MqttTopics.AnyMatch(topics, topic)) throw new InvalidDataException("Received a message outside the configured subscriptions.");
                if (qos == 1)
                {
                    if (start + 2 > body.Length) throw new InvalidDataException("Missing packet identifier.");
                    Send?.Invoke(new byte[] { 0x40, 2, body[start], body[start + 1] }); start += 2;
                }
                Payload?.Invoke(Encoding.UTF8.GetString(body, start, body.Length - start), topic, (header & 1) != 0);
            }
            else if (type == 13)
            { if (body.Length != 0) throw new InvalidDataException("Invalid PINGRESP."); awaitingPing = false; PingResponses++; }
            else if (type == 14) Fail("HiveMQ disconnected the MQTT session.");
            else throw new InvalidDataException("Unexpected MQTT packet type " + type + ".");
        }
        private static void Text(Stream stream, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text); stream.WriteByte((byte)(bytes.Length >> 8));
            stream.WriteByte((byte)bytes.Length); stream.Write(bytes, 0, bytes.Length);
        }
        public bool Publish(string topic, string json)
        {
            if (State != "CONNECTED" || string.IsNullOrWhiteSpace(topic) || topic.IndexOfAny(new[] {'+', '#', '\0'}) >= 0 ||
                Encoding.UTF8.GetByteCount(topic) > 65535 || Encoding.UTF8.GetByteCount(json ?? "") > 16384) return false;
            using var body = new MemoryStream(); Text(body, topic);
            var bytes = Encoding.UTF8.GetBytes(json ?? ""); body.Write(bytes, 0, bytes.Length);
            Send?.Invoke(Packet(0x30, body.ToArray())); return true;
        }
        public static byte[] Packet(byte header, byte[] body)
        {
            using var stream = new MemoryStream(); stream.WriteByte(header); int length = body.Length;
            do { int next = length % 128; length /= 128; if (length > 0) next |= 128; stream.WriteByte((byte)next); } while (length > 0);
            stream.Write(body, 0, body.Length); return stream.ToArray();
        }
    }

    public static class MqttTopics
    {
        public const string Defaults = "aquatwin/well/+/telemetry\ndigitaltwin/+/sensors";
        public static string[] Parse(string text) => (text ?? "").Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
        public static bool Validate(string text)
        {
            string[] filters = Parse(text);
            if (filters.Length == 0 || filters.Length > 8) return false;
            foreach (string raw in filters)
            {
                string filter = raw.Trim();
                if (filter.Length == 0 || Encoding.UTF8.GetByteCount(filter) > 512) return false;
                foreach (char c in filter) if (char.IsControl(c)) return false;
                string[] levels = filter.Split('/');
                for (int i = 0; i < levels.Length; i++)
                    if ((levels[i].Contains("#") && (levels[i] != "#" || i != levels.Length - 1)) ||
                        (levels[i].Contains("+") && levels[i] != "+")) return false;
            }
            return true;
        }
        public static bool AnyMatch(string[] filters, string topic)
        {
            if (string.IsNullOrEmpty(topic) || topic.Contains("+") || topic.Contains("#")) return false;
            foreach (string raw in filters)
            {
                string filter = raw.Trim();
                if (topic[0] == '$' && !filter.StartsWith("$")) continue;
                string[] f = filter.Split('/'), t = topic.Split('/'); int i = 0;
                while (i < f.Length)
                {
                    if (f[i] == "#") return true;
                    if (i >= t.Length || (f[i] != "+" && f[i] != t[i])) break;
                    i++;
                }
                if (i == f.Length && i == t.Length) return true;
            }
            return false;
        }
    }
}
