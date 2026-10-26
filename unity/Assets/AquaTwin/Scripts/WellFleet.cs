using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace AquaTwin
{
    /// <summary>Transport-independent, identity-keyed telemetry. No scene objects or fixed fleet size.</summary>
    public sealed class WellFleet
    {
        public readonly struct Sample
        {
            public readonly float Time, Depth;
            public Sample(float time, float depth) { Time = time; Depth = depth; }
        }

        public sealed class Well
        {
            public string Id { get; internal set; }
            public float DepthCm { get; internal set; }
            public float ColumnCm { get; internal set; }
            public float Tds { get; internal set; }
            public float Ph { get; internal set; } = float.NaN;
            public bool HasLocation { get; internal set; }
            public double Latitude { get; internal set; }
            public double Longitude { get; internal set; }
            public double SourceTimestamp { get; internal set; } = double.NaN;
            public float ReceivedAt { get; internal set; }
            public float AgeAtReceipt { get; internal set; }
            public bool ReportedOnline { get; internal set; }
            public bool AgeKnown { get; internal set; }
            public bool Demo { get; internal set; }
            public bool Retained { get; internal set; }
            public bool IsCached { get; internal set; }
            public bool UsesLegacyCalibration { get; internal set; }
            public float Fill => Math.Min(1f, Math.Max(0f, ColumnCm / Math.Max(0.1f, DepthCm + ColumnCm)));
            public readonly List<Sample> History = new();
            public float Age(float now) => AgeAtReceipt + Math.Max(0, now - ReceivedAt);
            public float ReceiveAge(float now) => Math.Max(0, now - ReceivedAt);
            public bool HasOldSourceTime(float now, float timeout) => !IsCached && ReportedOnline && !Retained &&
                ReceiveAge(now) <= timeout && AgeKnown && Age(now) > timeout;
            public bool IsFresh(float now, float timeout) => !IsCached && ReportedOnline && AgeKnown && Age(now) <= timeout;
            public bool NeedsReview(float tdsLimit, float lowFill) => Tds > tdsLimit || Fill < lowFill;
        }

        private readonly Dictionary<string, Well> byId = new(StringComparer.Ordinal);
        private readonly List<Well> ordered = new();
        public IReadOnlyList<Well> Wells => ordered;
        public int Revision { get; private set; }
        public Well Find(string id) => id != null && byId.TryGetValue(id, out Well well) ? well : null;
        public void Clear() { byId.Clear(); ordered.Clear(); Revision++; }
        public void RecalibrateLegacy(float fullDepthCm)
        {
            foreach (var well in ordered)
                if (well.UsesLegacyCalibration) well.ColumnCm = Math.Max(0, fullDepthCm - well.DepthCm);
            Revision++;
        }
        internal static Dictionary<string, object> ReadJsonObject(string json) => TelemetryJson.Read(json);

        public bool ApplyJson(string json, string topic, bool retained, float now, double utcSeconds,
            float legacyDepthCm, bool demo, out string error, bool subscribedTopic = false)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{"))
                    throw new FormatException("Expected a JSON telemetry object.");
                Dictionary<string, object> fields = TelemetryJson.Read(json);
                string wellId = StringField(fields, "well_id");
                string device = StringField(fields, "device");
                string status = StringField(fields, "status");
                double timestamp = NumberField(fields, "timestamp");
                float distance = (float)NumberField(fields, "distance_cm");
                float depthToWater = (float)NumberField(fields, "depth_to_water_cm");
                float columnHeight = (float)NumberField(fields, "water_column_height_cm");
                float tds = (float)NumberField(fields, "tds_ppm");
                float ph = (float)NumberField(fields, "ph");
                double latitude = double.NaN, longitude = double.NaN;
                if (fields.TryGetValue("location", out object locationValue))
                {
                    if (!(locationValue is Dictionary<string, object> location)) throw new FormatException("location must be an object.");
                    latitude = NumberField(location, "lat"); longitude = NumberField(location, "lng");
                    if (!ValidLocation(latitude, longitude)) throw new FormatException("Invalid location coordinates.");
                }
                bool canonical = !string.IsNullOrWhiteSpace(wellId);
                string id = canonical ? wellId : device;
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.IndexOfAny(new[] {'/', '+', '#', '\0'}) >= 0)
                    throw new FormatException("A valid well_id or device is required.");
                foreach (char character in id) if (char.IsControl(character)) throw new FormatException("Well IDs cannot contain control characters.");
                float depth = canonical ? depthToWater : distance;
                float column = canonical ? columnHeight : Math.Max(0, legacyDepthCm - depth);
                if (!Finite(depth) || depth < 0 || !Finite(column) || column < 0 ||
                    !Finite(tds) || tds < 0 ||
                    (!float.IsNaN(ph) && (!Finite(ph) || ph < 0 || ph > 14)))
                    throw new FormatException("Missing or invalid depth, water column, TDS, or pH.");
                if (canonical && (double.IsNaN(timestamp) || double.IsInfinity(timestamp) || timestamp < 0 ||
                    !ValidLocation(latitude, longitude) ||
                    (status != "ONLINE" && status != "OFFLINE")))
                    throw new FormatException("Canonical telemetry requires location, ONLINE/OFFLINE status and Unix timestamp.");
                // Live callers validate configured MQTT subscriptions first. Publisher topic
                // labels need not repeat the payload's well_id; strict mode remains the default.
                if (!subscribedTopic && topic != null && topic != (canonical ? "aquatwin/well/" + id + "/telemetry" : "digitaltwin/" + id + "/sensors"))
                    throw new FormatException("Topic identity does not match payload identity.");
                if (!double.IsNaN(timestamp) && (double.IsInfinity(timestamp) || timestamp < 0 || timestamp > utcSeconds + 300))
                    throw new FormatException("Invalid or future source timestamp (use Unix seconds).");
                Well well = Find(id);
                if (well != null && !double.IsNaN(well.SourceTimestamp) &&
                    !double.IsNaN(timestamp) && timestamp < well.SourceTimestamp && !(well.IsCached && !retained))
                    throw new FormatException("Out-of-order source timestamp.");
                if (well == null)
                {
                    well = new Well { Id = id };
                    byId.Add(id, well);
                    ordered.Add(well);
                    ordered.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                }
                well.DepthCm = depth;
                well.ColumnCm = column;
                well.Tds = tds;
                well.Ph = ph;
                well.HasLocation = ValidLocation(latitude, longitude);
                if (well.HasLocation) { well.Latitude = latitude; well.Longitude = longitude; }
                well.SourceTimestamp = timestamp;
                well.ReceivedAt = now;
                well.AgeKnown = !retained || !double.IsNaN(timestamp);
                well.AgeAtReceipt = double.IsNaN(timestamp) ? 0 : (float)Math.Max(0, utcSeconds - timestamp);
                well.ReportedOnline = !canonical || status == "ONLINE";
                well.Demo = demo;
                well.Retained = retained;
                well.IsCached = false;
                well.UsesLegacyCalibration = !canonical;
                // Retained snapshots are not a newly observed time-series sample.
                if (!retained && well.ReportedOnline)
                {
                    well.History.Add(new Sample(now - well.AgeAtReceipt, depth));
                    if (well.History.Count > 120) well.History.RemoveAt(0);
                }
                Revision++;
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool ValidLocation(double latitude, double longitude) => !double.IsNaN(latitude) && !double.IsNaN(longitude) &&
            latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180;
        private static string StringField(Dictionary<string, object> fields, string key)
        {
            if (!fields.TryGetValue(key, out object value)) return null;
            if (value is string text) return text;
            throw new FormatException(key + " must be a string.");
        }
        private static double NumberField(Dictionary<string, object> fields, string key)
        {
            if (!fields.TryGetValue(key, out object value)) return double.NaN;
            if (value is double number) return number;
            throw new FormatException(key + " must be a JSON number.");
        }

        // Small strict JSON reader keeps the telemetry domain portable and rejects coercion,
        // duplicate keys, non-finite numbers, trailing data and excessive nesting.
        private sealed class TelemetryJson
        {
            private static readonly Regex Number = new(@"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant);
            private readonly string text;
            private int cursor;
            private TelemetryJson(string text) { this.text = text; }
            public static Dictionary<string, object> Read(string json)
            {
                var reader = new TelemetryJson(json);
                object value = reader.Value(0);
                reader.Space();
                if (reader.cursor != json.Length || !(value is Dictionary<string, object> result))
                    throw new FormatException("Expected one JSON object.");
                return result;
            }
            private void Space() { while (cursor < text.Length && (text[cursor] == ' ' || text[cursor] == '\t' || text[cursor] == '\r' || text[cursor] == '\n')) cursor++; }
            private bool Take(char value) { Space(); if (cursor >= text.Length || text[cursor] != value) return false; cursor++; return true; }
            private void Expect(char value) { if (!Take(value)) throw new FormatException("Malformed JSON: expected " + value); }
            private object Value(int depth)
            {
                if (depth > 32) throw new FormatException("JSON nesting limit exceeded.");
                Space();
                if (cursor >= text.Length) throw new FormatException("Incomplete JSON value.");
                if (text[cursor] == '{')
                {
                    cursor++;
                    var result = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (Take('}')) return result;
                    do
                    {
                        Space(); string key = ReadString(); Expect(':');
                        if (result.ContainsKey(key)) throw new FormatException("Duplicate JSON key: " + key);
                        result.Add(key, Value(depth + 1));
                        if (Take('}')) return result;
                        Expect(',');
                    } while (true);
                }
                if (text[cursor] == '[')
                {
                    cursor++;
                    var result = new List<object>();
                    if (Take(']')) return result;
                    do { result.Add(Value(depth + 1)); if (Take(']')) return result; Expect(','); } while (true);
                }
                if (text[cursor] == '\"') return ReadString();
                int start = cursor;
                while (cursor < text.Length && text[cursor] != ',' && text[cursor] != '}' && text[cursor] != ']' && !char.IsWhiteSpace(text[cursor])) cursor++;
                string token = text.Substring(start, cursor - start);
                if (token == "null") return null;
                if (token == "true") return true;
                if (token == "false") return false;
                if (!Number.IsMatch(token) || !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || double.IsNaN(number) || double.IsInfinity(number))
                    throw new FormatException("Invalid JSON number or literal.");
                return number;
            }
            private string ReadString()
            {
                Expect('\"');
                var output = new StringBuilder();
                while (cursor < text.Length)
                {
                    char value = text[cursor++];
                    if (value == '\"') return output.ToString();
                    if (value < 32) throw new FormatException("Unescaped control character.");
                    if (value != '\\') { output.Append(value); continue; }
                    if (cursor >= text.Length) break;
                    char escape = text[cursor++];
                    switch (escape)
                    {
                        case '\"': case '\\': case '/': output.Append(escape); break;
                        case 'b': output.Append('\b'); break;
                        case 'f': output.Append('\f'); break;
                        case 'n': output.Append('\n'); break;
                        case 'r': output.Append('\r'); break;
                        case 't': output.Append('\t'); break;
                        case 'u':
                            if (cursor + 4 > text.Length || !ushort.TryParse(text.Substring(cursor, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                                throw new FormatException("Invalid JSON unicode escape.");
                            output.Append((char)code); cursor += 4; break;
                        default: throw new FormatException("Invalid JSON escape.");
                    }
                }
                throw new FormatException("Unterminated JSON string.");
            }
        }
    }
}
