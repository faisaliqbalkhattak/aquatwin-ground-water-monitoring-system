using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AquaTwin
{
    /// <summary>Offline keyframe playback; emitted packets use the same contract as live wells.</summary>
    public sealed class DemoTelemetryDataset
    {
        private sealed class Frame
        {
            public float Time, Depth, Column, Tds, Ph;
            public bool Online, Transmit;
        }
        private sealed class DemoWell
        {
            public string Id;
            public double Latitude, Longitude;
            public readonly List<Frame> Frames = new();
        }
        private readonly List<DemoWell> wells = new();
        public int WellCount => wells.Count;
        public float DurationSeconds { get; private set; }
        public float SampleIntervalSeconds { get; private set; }

        public static DemoTelemetryDataset Load(string json)
        {
            var root = WellFleet.ReadJsonObject(json);
            var dataset = new DemoTelemetryDataset
            {
                DurationSeconds = (float)Number(root, "duration_seconds"),
                SampleIntervalSeconds = (float)Number(root, "sample_interval_seconds")
            };
            if (dataset.DurationSeconds <= 0 || dataset.SampleIntervalSeconds <= 0)
                throw new FormatException("Demo duration and sample interval must be positive.");
            if (!(root["wells"] is List<object> entries) || entries.Count == 0)
                throw new FormatException("Demo requires a wells array.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object entry in entries)
            {
                var fields = Object(entry);
                var location = Object(fields["location"]);
                var well = new DemoWell { Id = (string)fields["well_id"], Latitude = Number(location, "lat"), Longitude = Number(location, "lng") };
                if (!ids.Add(well.Id)) throw new FormatException("Demo well IDs must be unique.");
                if (!(fields["samples"] is List<object> samples) || samples.Count < 2)
                    throw new FormatException("Each demo well needs at least two samples.");
                foreach (object sample in samples)
                {
                    var values = Object(sample);
                    string status = (string)values["status"];
                    if (status != "ONLINE" && status != "OFFLINE") throw new FormatException("Invalid demo status.");
                    var frame = new Frame
                    {
                        Time = (float)Number(values, "at_seconds"), Depth = (float)Number(values, "depth_to_water_cm"),
                        Column = (float)Number(values, "water_column_height_cm"), Tds = (float)Number(values, "tds_ppm"),
                        Ph = values.ContainsKey("ph") ? (float)Number(values, "ph") : float.NaN,
                        Online = status == "ONLINE", Transmit = !values.ContainsKey("transmit") || (bool)values["transmit"]
                    };
                    if (frame.Time < 0 || frame.Time > dataset.DurationSeconds ||
                        well.Frames.Count > 0 && frame.Time <= well.Frames[well.Frames.Count - 1].Time)
                        throw new FormatException("Demo sample times must increase within the playback duration.");
                    // Validate every keyframe with the production telemetry parser before playback starts.
                    string packet = Packet(well, frame.Depth, frame.Column, frame.Tds, frame.Ph, frame.Online, 1000);
                    if (!new WellFleet().ApplyJson(packet, null, false, 0, 1000, 20, true, out string error))
                        throw new FormatException("Invalid demo keyframe: " + error);
                    well.Frames.Add(frame);
                }
                dataset.wells.Add(well);
            }
            return dataset;
        }

        public int Emit(WellFleet target, float playhead, float now, double utcSeconds, int requestedWells)
        {
            int emitted = 0;
            for (int i = 0; i < Math.Min(Math.Max(0, requestedWells), wells.Count); i++)
            {
                DemoWell well = wells[i];
                int index = -1;
                for (int j = 0; j < well.Frames.Count && well.Frames[j].Time <= playhead; j++) index = j;
                if (index < 0) continue; // A late-arriving well is discovered only when its first packet exists.
                Frame current = well.Frames[index];
                if (!current.Transmit) continue; // Deliberately silent sensor: normal freshness logic makes it stale.
                Frame next = well.Frames[Math.Min(index + 1, well.Frames.Count - 1)];
                float blend = next.Time > current.Time ? Math.Max(0, Math.Min(1, (playhead - current.Time) / (next.Time - current.Time))) : 0;
                float Mix(float a, float b) => a + (b - a) * blend;
                float ph = float.IsNaN(current.Ph) || float.IsNaN(next.Ph) ? current.Ph : Mix(current.Ph, next.Ph);
                string packet = Packet(well, Mix(current.Depth, next.Depth), Mix(current.Column, next.Column),
                    Mix(current.Tds, next.Tds), ph, current.Online, utcSeconds);
                if (!target.ApplyJson(packet, null, false, now, utcSeconds, 20, true, out string error))
                    throw new FormatException("Demo playback rejected: " + error);
                emitted++;
            }
            return emitted;
        }

        private static Dictionary<string, object> Object(object value) => value as Dictionary<string, object> ?? throw new FormatException("Expected demo JSON object.");
        private static double Number(Dictionary<string, object> fields, string name)
        {
            if (fields.TryGetValue(name, out object value) && value is double number && !double.IsInfinity(number) && !double.IsNaN(number)) return number;
            throw new FormatException("Demo field " + name + " must be a finite number.");
        }
        private static string Packet(DemoWell well, float depth, float column, float tds, float ph, bool online, double utc)
        {
            string id = well.Id.Replace("\\", "\\\\").Replace("\"", "\\\"");
            // Mono and modern .NET interpret escaped closing braces beside composite
            // numeric format items differently. Format each scalar independently so
            // JSON punctuation can never become part of its numeric format string.
            var packet = new StringBuilder(256);
            packet.Append("{\"well_id\":\"").Append(id)
                .Append("\",\"location\":{\"lat\":").Append(well.Latitude.ToString("R", CultureInfo.InvariantCulture))
                .Append(",\"lng\":").Append(well.Longitude.ToString("R", CultureInfo.InvariantCulture))
                .Append("},\"depth_to_water_cm\":").Append(depth.ToString("R", CultureInfo.InvariantCulture))
                .Append(",\"water_column_height_cm\":").Append(column.ToString("R", CultureInfo.InvariantCulture))
                .Append(",\"tds_ppm\":").Append(tds.ToString("R", CultureInfo.InvariantCulture));
            if (!float.IsNaN(ph)) packet.Append(",\"ph\":").Append(ph.ToString("R", CultureInfo.InvariantCulture));
            packet.Append(",\"status\":\"").Append(online ? "ONLINE" : "OFFLINE")
                .Append("\",\"timestamp\":").Append(utc.ToString("R", CultureInfo.InvariantCulture)).Append('}');
            return packet.ToString();
        }
    }
}
