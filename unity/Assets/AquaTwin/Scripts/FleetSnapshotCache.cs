using System;
using System.Collections.Generic;
using System.Text;

namespace AquaTwin
{
    /// <summary>Original validated readings, scoped to a broker/account. Restores never claim live reporting.</summary>
    public sealed class FleetSnapshotCache
    {
        private readonly Dictionary<string, (string topic, string json)> entries = new(StringComparer.Ordinal);
        public string Scope { get; }
        public int Count => entries.Count;
        public FleetSnapshotCache(string scope) { Scope = scope; }
        public void Remember(string json, string topic)
        {
            var fields = WellFleet.ReadJsonObject(json);
            string id = fields.TryGetValue("well_id", out var canonical) ? canonical as string : null;
            if (string.IsNullOrWhiteSpace(id) && fields.TryGetValue("device", out var legacy)) id = legacy as string;
            if (!string.IsNullOrEmpty(id)) entries[id] = (topic, json);
        }
        public string Encode()
        {
            var text = new StringBuilder("{\"scope\":");
            AquaTwinSettingsCodec.AppendString(text, Scope); text.Append(",\"readings\":[");
            foreach (var item in entries.Values)
            {
                text.Append("{\"topic\":"); AquaTwinSettingsCodec.AppendString(text, item.topic ?? "");
                text.Append(",\"json\":"); AquaTwinSettingsCodec.AppendString(text, item.json); text.Append("},");
            }
            if (entries.Count > 0) text.Length--;
            return text.Append("]}").ToString();
        }
        public int Restore(string encoded, WellFleet fleet, string[] topics, float now, double utc, float legacyDepth)
        {
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 1024 * 1024) return 0;
            var document = WellFleet.ReadJsonObject(encoded);
            if (!document.TryGetValue("scope", out var scope) || !(scope is string savedScope) || savedScope != Scope ||
                !document.TryGetValue("readings", out var readings) || !(readings is List<object> list)) return 0;
            int restored = 0;
            foreach (var value in list)
            {
                if (!(value is Dictionary<string, object> record) ||
                    !record.TryGetValue("json", out var raw) || !(raw is string json) ||
                    !record.TryGetValue("topic", out var channel) || !(channel is string topic) ||
                    !MqttTopics.AnyMatch(topics, topic)) continue;
                Dictionary<string, object> fields;
                try { fields = WellFleet.ReadJsonObject(json); }
                catch (FormatException) { continue; }
                string id = fields.TryGetValue("well_id", out var canonical) ? canonical as string : null;
                if (string.IsNullOrWhiteSpace(id) && fields.TryGetValue("device", out var legacy)) id = legacy as string;
                // Never overwrite a measurement received live in this running session.
                if (fleet.Find(id) != null) continue;
                if (!fleet.ApplyJson(json, topic, true, now, utc, legacyDepth, false, out _, true)) continue;
                fleet.Find(id).IsCached = true;
                Remember(json, topic); restored++;
            }
            return restored;
        }
    }
}
