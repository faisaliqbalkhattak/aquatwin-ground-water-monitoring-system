using System;
using System.Collections.Generic;
using System.Text;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif

namespace AquaTwin.Editor
{
    public static class MqttValidation
    {
#if UNITY_EDITOR
        [MenuItem("AquaTwin/Validate MQTT Transport", priority = 33)]
#endif
        public static void Run()
        {
            int checks = 0;
            void Check(bool ok, string message) { if (!ok) throw new Exception("MQTT validation: " + message); checks++; }
            string[] topics = { "aquatwin/well/+/telemetry", "digitaltwin/+/sensors" };
            MqttProtocolSession Session(out List<byte[]> sent)
            {
                var output = new List<byte[]>(); sent = output;
                return new MqttProtocolSession("user", "password", topics, 0) { Send = bytes => output.Add(bytes) };
            }
            var session = Session(out var sent);
            session.Open(0);
            Check(session.State == "AUTHENTICATING" && sent.Count == 1 && sent[0][0] == 0x10, "CONNECT sent on open");
            session.Feed(new byte[] { 0x20 }, 1);
            Check(session.State == "AUTHENTICATING", "fragmented header waits");
            session.Feed(new byte[] { 2, 0, 0 }, 1);
            Check(session.State == "SUBSCRIBING" && sent[1][0] == 0x82, "successful CONNACK subscribes");
            string received = null, receivedTopic = null; bool retained = false;
            session.Payload = (json, topic, retain) => { received = json; receivedTopic = topic; retained = retain; };
            const string payload = "{\"well_id\":\"WELL_WOKWI_01\",\"location\":{\"lat\":24.453900,\"lng\":54.377300},\"depth_to_water_cm\":143.14,\"water_column_height_cm\":356.86,\"tds_ppm\":320.3,\"status\":\"ONLINE\",\"timestamp\":1791700194}";
            const string topicName = "aquatwin/well/WELL_01/telemetry";
            byte[] topicBytes = Encoding.UTF8.GetBytes(topicName), jsonBytes = Encoding.UTF8.GetBytes(payload);
            var body = new List<byte> { (byte)(topicBytes.Length >> 8), (byte)topicBytes.Length };
            body.AddRange(topicBytes); body.AddRange(jsonBytes);
            byte[] publish = MqttProtocolSession.Packet(0x31, body.ToArray());
            // Retained PUBLISH can precede SUBACK, and can be split at every byte boundary.
            foreach (byte b in publish) session.Feed(new[] { b }, 2);
            Check(received == payload && receivedTopic == topicName && retained, "fragmented retained PUBLISH before SUBACK");
            session.Feed(new byte[] { 0x90, 4, 0, 1, 0, 0 }, 2);
            Check(session.State == "CONNECTED", "all subscriptions acknowledged");
            session.Tick(17);
            Check(sent[sent.Count - 1][0] == 0xC0, "keep-alive sends PINGREQ");
            session.Feed(new byte[] { 0xD0, 0 }, 18); session.Tick(32);
            Check(session.State == "CONNECTED", "PINGRESP keeps session alive");
            session.Tick(62);
            Check(session.State == "OFFLINE" && session.Detail.Contains("keep-alive"), "missing PINGRESP times out");
            // A quiet sensor must not reconnect a healthy MQTT session. It must
            // still accept a runtime-sized fleet when the publisher starts later.
            var quiet = Session(out var quietSent);
            quiet.Open(0); quiet.Feed(new byte[] { 0x20, 2, 0, 0, 0x90, 4, 0, 1, 0, 0 }, 1);
            for (int tick = 16; tick <= 601; tick += 15)
            { quiet.Tick(tick); quiet.Feed(new byte[] { 0xD0, 0 }, tick + .1); }
            Check(quiet.State == "CONNECTED" && quiet.PingResponses == 40, "ten quiet minutes retain the authenticated session via keep-alive");
            Check(quietSent.FindAll(frame => frame[0] == 0x10).Count == 1 && quietSent.FindAll(frame => frame[0] == 0x82).Count == 1,
                "quiet period sends one CONNECT and one SUBSCRIBE, without renewal loops");
            var delayedFleet = new WellFleet();
            quiet.Payload = (json, topic, retain) => {
                if (!delayedFleet.ApplyJson(json, topic, retain, 602, 1791700194, 20, false, out string problem, true)) throw new Exception(problem);
            };
            foreach (string id in new[] { "WELL_WOKWI_01", "WELL_SIM_02", "WELL_SIM_03", "LATE_WELL_04" })
            {
                var channel = Encoding.UTF8.GetBytes("aquatwin/well/" + id + "/telemetry");
                var frameBody = new List<byte> { (byte)(channel.Length >> 8), (byte)channel.Length };
                frameBody.AddRange(channel); frameBody.AddRange(Encoding.UTF8.GetBytes(payload.Replace("WELL_WOKWI_01", id)));
                quiet.Feed(MqttProtocolSession.Packet(0x30, frameBody.ToArray()), 602);
            }
            Check(delayedFleet.Wells.Count == 4 && quiet.State == "CONNECTED", "late sensor startup discovers three wells and an additional runtime identity without reconnecting");
            quiet.Tick(631); quiet.Tick(661);
            Check(quiet.State == "OFFLINE" && quiet.Detail.Contains("keep-alive"), "actual missing keep-alive response still fails for automatic retry");
            for (int code = 1; code <= 5; code++)
            {
                session = Session(out sent); session.Open(0); session.Feed(new byte[] { 0x20, 2, 0, (byte)code }, 1);
                Check(session.State == "OFFLINE" && session.Detail.Contains("CONNACK " + code), "authentication rejection " + code);
            }
            session = Session(out sent); session.Open(0); session.Feed(new byte[] { 0x20, 2, 0, 0, 0x90, 4, 0, 1, 0, 128 }, 1);
            Check(session.State == "OFFLINE" && session.Detail.Contains(topics[1]), "coalesced SUBACK denial identifies topic");
            session = Session(out sent); session.Tick(12);
            Check(session.State == "OFFLINE" && session.Detail.Contains("WebSocket timed out"), "socket opening timeout");
            session = Session(out sent); session.Open(0); session.Tick(12);
            Check(session.State == "OFFLINE" && session.Detail.Contains("CONNACK"), "authentication timeout");
            session = Session(out sent); session.Open(0); session.Feed(new byte[] { 0x20, 2, 0, 0 }, 1); session.Tick(13);
            Check(session.State == "OFFLINE" && session.Detail.Contains("SUBACK"), "subscription timeout");
            session = Session(out sent); session.Open(0); session.Feed(new byte[] { 0x20, 128, 128, 128, 128 }, 1);
            Check(session.State == "OFFLINE", "malformed remaining length rejected");
            session = Session(out sent); session.Open(0); session.Feed(new byte[] { 0x20, 2, 0, 0 }, 1);
            session.Feed(MqttProtocolSession.Packet(0x30, new byte[] { 0, 10, 1 }), 2);
            Check(session.State == "OFFLINE", "truncated publish rejected");
            session = new MqttProtocolSession("user", "", topics, 0); session.Open(0);
            Check(session.State == "OFFLINE" && session.Detail.Contains("Credentials"), "missing credentials actionable");
            Check(MqttTopics.AnyMatch(topics, topicName), "user topic matches default fleet filter");
            Check(!MqttTopics.AnyMatch(topics, "aquatwin/well/A/B/telemetry"), "+ matches exactly one level");
            Check(MqttTopics.AnyMatch(new[] { "a/#" }, "a") && MqttTopics.AnyMatch(new[] { "a/#" }, "a/b/c"), "# matches zero or many levels");
            Check(!MqttTopics.AnyMatch(new[] { "#" }, "$SYS/info"), "wildcard excludes system topics");
            Check(MqttTopics.Validate("custom/exact\naquatwin/well/+/telemetry"), "custom topic accepted");
            foreach (string invalid in new[] { "", "a/#/b", "a/b+", "a/#b", "a/\0" })
                Check(!MqttTopics.Validate(invalid), "invalid filter rejected");
            var fleet = new WellFleet();
            Check(fleet.ApplyJson(payload, topicName, false, 0, 1791700194, 20, false, out _, true), "exact user payload with different topic identity");
            var well = fleet.Find("WELL_WOKWI_01");
            Check(well != null && Math.Abs(well.DepthCm - 143.14f) < .001 && Math.Abs(well.ColumnCm - 356.86f) < .001, "centimetres preserved");
            Check(Math.Abs(well.Fill - .71372f) < .0001 && Math.Abs(well.Longitude - 54.3773) < .000001 && float.IsNaN(well.Ph), "fill, coordinates and optional pH");
            Check(fleet.ApplyJson(payload, topicName, true, 0, 1791700194 + 86400, 20, false, out _, true) && !well.IsFresh(0, 15), "old timestamp accepted as stale");
            var settings = new AquaTwinMqttSettingsData { telemetryTopics = "custom/exact", password = "secret" };
            Check(AquaTwinSettingsCodec.Decode(AquaTwinSettingsCodec.EncodeForStorage(settings)).telemetryTopics == "custom/exact", "topic persists");
            var copy = settings.Copy(); copy.telemetryTopics = "new/exact";
            Check(!settings.SameConnection(copy), "topic edit triggers reconnect");
            Check(AquaTwinSettingsCodec.Decode("{}").telemetryTopics == MqttTopics.Defaults, "old saved settings migrate topics");
            settings.brokerPort = 8883;
            Check(settings.TryValidate(out _, out _) && settings.brokerPort == 8883 && settings.Endpoint.Port == 8884 && settings.UsesDevicePort, "device port accepted while dashboard uses WSS");
            Check(AquaTwinSettingsCodec.Decode(AquaTwinSettingsCodec.EncodeForStorage(settings)).brokerPort == 8883, "device port selection persists without silent rewrite");
            var custom = settings.Copy(); custom.brokerHost = "custom.example.org";
            Check(custom.Endpoint.Port == 8883 && !custom.UsesDevicePort, "custom WSS port preserved");
            var migrated = AquaTwinSettingsCodec.Decode("{\"brokerPort\":8883,\"username\":\"existing-user\",\"password\":\"existing-password\"}");
            Check(migrated.brokerPort == 8883 && migrated.Endpoint.Port == 8884 && migrated.username == "existing-user" && migrated.password == "existing-password", "legacy device settings resolve WSS without losing credentials");
            var connection = new FleetSession(); connection.SetConnection("CONNECTED", "Subscribed");
            Check(connection.LiveDataStatus(0, 15) == "BROKER CONNECTED · NO SENSOR MESSAGES", "broker login cannot imply sensor online");
            connection.SetDeliveryCounts(1, 1);
            Check(connection.LiveDataStatus(0, 15) == "MESSAGES RECEIVED · TELEMETRY REJECTED", "rejected payload distinct from no publisher");
            connection.SetConnection("OFFLINE", "Socket closed");
            Check(connection.LiveDataStatus(0, 15) == "NO SENSOR DATA RECEIVED", "broker offline distinguished from no data");
            connection.SetConnection("CONNECTED", "Subscribed"); connection.SetDeliveryCounts(1, 0);
            Check(connection.LiveFleet.ApplyJson(payload, "aquatwin/well/WELL_WOKWI_01/telemetry", false, 0, 1791700194 + 480, 20, false, out _, true), "Wokwi delayed source timestamp accepted");
            Check(connection.LiveFleet.Find("WELL_WOKWI_01").HasOldSourceTime(1, 15), "recent arrival distinguished from old source clock");
            Check(connection.LiveDataStatus(1, 15) == "RECEIVING · SENSOR TIMESTAMP STALE", "incoming stale data not described as missing sensor messages");
            Check(connection.LiveDataStatus(20, 15) == "NO FRESH SENSOR DATA", "stopped stream does not remain receiving");
            var snapshot = new WellFleet(); snapshot.ApplyJson(payload, null, true, 0, 1791700194 + 480, 20, false, out _);
            Check(!snapshot.Find("WELL_WOKWI_01").HasOldSourceTime(1, 15), "retained snapshot never presented as current incoming stream");
#if UNITY_EDITOR
            Debug.Log("AQUATWIN_MQTT_CHECKS_PASSED: " + checks);
#else
            Console.WriteLine("AQUATWIN_MQTT_CHECKS_PASSED: " + checks);
#endif
        }
    }
}
