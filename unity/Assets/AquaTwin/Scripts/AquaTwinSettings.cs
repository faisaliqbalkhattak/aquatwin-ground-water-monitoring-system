using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AquaTwin
{
    public enum AquaTwinDataMode { Simulation, LiveMqtt }

    [Serializable]
    public sealed class AquaTwinMqttSettingsData
    {
        // Retained for compatibility with older saved settings.
        public AquaTwinDataMode mode = AquaTwinDataMode.LiveMqtt;
        public int demoWellCount = 10;
        public string brokerHost = "6a3cba0faab347c9a1a0c5afcfbd3ca3.s1.eu.hivemq.cloud";
        public int brokerPort = 8884;
        public string webSocketPath = "/mqtt";
        public string username = "well_dt_esp32";
        public string password = "";
        public bool rememberPassword;
        public bool applyToDigitalTwin = true;
        public float legacyWellDepthCm = 20;
        public float staleAfterSeconds = 15;
        public float reviewTdsPpm = 600;
        public float lowWaterPercent = 20;
        public AquaTwinMqttSettingsData Copy() => (AquaTwinMqttSettingsData)MemberwiseClone();
        public Uri Endpoint => new UriBuilder("wss", brokerHost, brokerPort, webSocketPath).Uri;
        public void Normalize()
        {
            brokerHost = (brokerHost ?? "").Trim();
            webSocketPath = (webSocketPath ?? "").Trim();
            username = (username ?? "").Trim();
            password ??= "";
        }
        public bool TryValidate(out string field, out string error)
        {
            field = null; error = null;
            if (string.IsNullOrWhiteSpace(brokerHost) || brokerHost.IndexOfAny(new[] { '/', ':', '?', '#', '@', ' ', '\\' }) >= 0 || Uri.CheckHostName(brokerHost) == UriHostNameType.Unknown)
            { field = "brokerHost"; error = "Enter a broker hostname without wss://, a port or a path."; }
            else if (brokerPort < 1 || brokerPort > 65535)
            { field = "brokerPort"; error = "Port must be between 1 and 65535."; }
            else if (string.IsNullOrWhiteSpace(webSocketPath) || !webSocketPath.StartsWith("/") || webSocketPath.IndexOfAny(new[] { '?', '#', ' ', '\\' }) >= 0)
            { field = "webSocketPath"; error = "Enter a WebSocket path beginning with /, such as /mqtt."; }
            else if (Encoding.UTF8.GetByteCount(username ?? "") > 65535 || (username ?? "").IndexOf('\0') >= 0)
            { field = "username"; error = "Enter a valid MQTT username."; }
            else if (Encoding.UTF8.GetByteCount(password ?? "") > 65535 || (password ?? "").IndexOf('\0') >= 0)
            { field = "password"; error = "Enter a valid MQTT password."; }
            else if (!Finite(legacyWellDepthCm) || legacyWellDepthCm < .1f)
            { field = "legacyWellDepthCm"; error = "Legacy calibration must be at least 0.1 cm."; }
            else if (!Finite(staleAfterSeconds) || staleAfterSeconds < 3)
            { field = "staleAfterSeconds"; error = "The stale timeout must be at least 3 seconds."; }
            else if (!Finite(reviewTdsPpm) || reviewTdsPpm < 0)
            { field = "reviewTdsPpm"; error = "The TDS threshold must be zero or higher."; }
            else if (!Finite(lowWaterPercent) || lowWaterPercent < 0 || lowWaterPercent > 100)
            { field = "lowWaterPercent"; error = "The low-water threshold must be between 0 and 100%."; }
            return error == null;
        }
        public bool SameConnection(AquaTwinMqttSettingsData other) => other != null &&
            brokerHost == other.brokerHost && brokerPort == other.brokerPort && webSocketPath == other.webSocketPath &&
            username == other.username && password == other.password;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>Portable settings encoding shared by desktop files and WebGL browser storage.</summary>
    public static class AquaTwinSettingsCodec
    {
        public static AquaTwinMqttSettingsData Decode(string json)
        {
            var fields = WellFleet.ReadJsonObject(json);
            var data = new AquaTwinMqttSettingsData();
            string Text(string key, string fallback) => fields.TryGetValue(key, out object value) ? value as string ?? throw new FormatException("Invalid settings string.") : fallback;
            double Number(string key, double fallback) => fields.TryGetValue(key, out object value) ? value is double number ? number : throw new FormatException("Invalid settings number.") : fallback;
            bool Flag(string key, bool fallback) => fields.TryGetValue(key, out object value) ? value is bool flag ? flag : throw new FormatException("Invalid settings flag.") : fallback;
            data.brokerHost = Text("brokerHost", data.brokerHost);
            double port = Number("brokerPort", data.brokerPort);
            if (port != Math.Truncate(port) || port < 1 || port > 65535) throw new FormatException("Invalid broker port.");
            data.brokerPort = (int)port;
            data.webSocketPath = Text("webSocketPath", data.webSocketPath);
            data.username = Text("username", data.username);
            data.password = Text("password", "");
            // Preserve the user's previous explicit credential-file behaviour when migrating old files.
            data.rememberPassword = Flag("rememberPassword", !string.IsNullOrEmpty(data.password));
            data.applyToDigitalTwin = Flag("applyToDigitalTwin", true);
            data.legacyWellDepthCm = (float)Number("legacyWellDepthCm", data.legacyWellDepthCm);
            data.staleAfterSeconds = (float)Number("staleAfterSeconds", data.staleAfterSeconds);
            data.reviewTdsPpm = (float)Number("reviewTdsPpm", data.reviewTdsPpm);
            data.lowWaterPercent = (float)Number("lowWaterPercent", data.lowWaterPercent);
            data.Normalize();
            if (!data.TryValidate(out _, out string error)) throw new FormatException(error);
            return data;
        }
        public static string EncodeForStorage(AquaTwinMqttSettingsData data)
        {
            if (!data.TryValidate(out _, out string error)) throw new ArgumentException(error);
            var output = new StringBuilder("{");
            void Text(string key, string value) { output.Append('"').Append(key).Append("\":"); AppendString(output, value ?? ""); output.Append(','); }
            void Number(string key, double value) { output.Append('"').Append(key).Append("\":").Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(','); }
            void Flag(string key, bool value) { output.Append('"').Append(key).Append("\":").Append(value ? "true" : "false").Append(','); }
            Text("brokerHost", data.brokerHost); Number("brokerPort", data.brokerPort); Text("webSocketPath", data.webSocketPath);
            Text("username", data.username); Text("password", data.rememberPassword ? data.password : "");
            Flag("rememberPassword", data.rememberPassword); Flag("applyToDigitalTwin", data.applyToDigitalTwin);
            Number("legacyWellDepthCm", data.legacyWellDepthCm); Number("staleAfterSeconds", data.staleAfterSeconds);
            Number("reviewTdsPpm", data.reviewTdsPpm); Number("lowWaterPercent", data.lowWaterPercent);
            output.Length--; output.Append('}'); return output.ToString();
        }
        private static void AppendString(StringBuilder output, string value)
        {
            output.Append('"');
            foreach (char character in value)
            {
                if (character == '"' || character == '\\') output.Append('\\').Append(character);
                else if (character < 32) output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                else output.Append(character);
            }
            output.Append('"');
        }
    }

    public static class AquaTwinSettingsFileStore
    {
        public static void WriteAtomic(string path, string json)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                if (File.Exists(path)) File.Replace(temporary, path, null, true);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
