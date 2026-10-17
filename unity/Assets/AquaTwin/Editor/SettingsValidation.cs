using System;
using System.Globalization;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif

namespace AquaTwin.Editor
{
    public static class SettingsValidation
    {
#if UNITY_EDITOR
        [MenuItem("AquaTwin/Validate Settings", priority = 32)]
#endif
        public static void Run()
        {
            int checks = 0;
            void Check(bool pass, string message) { if (!pass) throw new Exception("Settings validation: " + message); checks++; }
            var original = new AquaTwinMqttSettingsData();
            Check(original.TryValidate(out _, out _) && !original.rememberPassword, "valid defaults keep password session-only");
            original.username = "demo\"operator\\name";
            original.password = "test-only\"value\\line\n";
            original.legacyWellDepthCm = 32.5f; original.lowWaterPercent = 12.75f;
            var decoded = AquaTwinSettingsCodec.Decode(AquaTwinSettingsCodec.EncodeForStorage(original));
            Check(decoded.username == original.username && decoded.password == "", "persistent preferences escape text and omit unremembered password");
            Check(original.password.Length > 0 && !decoded.rememberPassword, "storage encoding never clears the active session password");
            Check(decoded.legacyWellDepthCm == 32.5f && decoded.lowWaterPercent == 12.75f, "monitoring preferences round-trip");
            original.rememberPassword = true;
            decoded = AquaTwinSettingsCodec.Decode(AquaTwinSettingsCodec.EncodeForStorage(original));
            Check(decoded.password == original.password && decoded.rememberPassword, "explicit password persistence round-trips escapes");
            var legacy = AquaTwinSettingsCodec.Decode("{\"mode\":0,\"username\":\"existing\",\"password\":\"test-only\",\"applyToDigitalTwin\":false}");
            Check(legacy.username == "existing" && legacy.rememberPassword && !legacy.applyToDigitalTwin, "legacy local credentials and live toggle migrate");
            Check(legacy.brokerPort == 8884 && legacy.staleAfterSeconds == 15, "new fields receive defaults when absent from old settings");
            var draft = original.Copy(); draft.username = "changed";
            Check(original.username != draft.username, "draft editing/cancel cannot mutate current settings");
            draft = original.Copy(); draft.reviewTdsPpm = 900;
            Check(original.SameConnection(draft), "monitoring changes do not require a broker reconnect");
            draft.brokerHost = "example.com";
            Check(!original.SameConnection(draft), "broker changes require reconnect");
            draft.password = "new-test-value";
            Check(!original.SameConnection(draft), "credential changes require reconnect");
            var endpoint = new AquaTwinMqttSettingsData { brokerHost = "example.com", brokerPort = 443, webSocketPath = "/mqtt" };
            Check(endpoint.Endpoint.AbsoluteUri == "wss://example.com/mqtt", "custom endpoint uses WSS and the configured port/path");
            endpoint.brokerHost = " example.com "; endpoint.webSocketPath = " /mqtt "; endpoint.Normalize();
            Check(endpoint.brokerHost == "example.com" && endpoint.webSocketPath == "/mqtt", "form normalization");
            string Invalid(Action<AquaTwinMqttSettingsData> edit)
            {
                var value = new AquaTwinMqttSettingsData(); edit(value);
                return value.TryValidate(out string field, out _) ? null : field;
            }
            Check(Invalid(x => x.brokerHost = "wss://example.com") == "brokerHost", "host cannot accidentally include a URL scheme");
            Check(Invalid(x => x.brokerPort = 70000) == "brokerPort", "port bounds");
            Check(Invalid(x => x.webSocketPath = "mqtt") == "webSocketPath", "WebSocket path validation");
            Check(Invalid(x => x.legacyWellDepthCm = 0) == "legacyWellDepthCm", "positive calibration");
            Check(Invalid(x => x.staleAfterSeconds = 1) == "staleAfterSeconds", "stale timeout bounds");
            Check(Invalid(x => x.reviewTdsPpm = float.NaN) == "reviewTdsPpm", "non-finite threshold rejected");
            Check(Invalid(x => x.lowWaterPercent = 101) == "lowWaterPercent", "percentage bounds");
            bool malformedRejected = false;
            try { AquaTwinSettingsCodec.Decode("{\"brokerPort\":\"8884\"}"); } catch (FormatException) { malformedRejected = true; }
            Check(malformedRejected, "wrong persisted types rejected");
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                decoded = AquaTwinSettingsCodec.Decode(AquaTwinSettingsCodec.EncodeForStorage(original));
                Check(decoded.lowWaterPercent == 12.75f, "persistent JSON is culture invariant on Mono and .NET");
            }
            finally { CultureInfo.CurrentCulture = previousCulture; }
            var fleet = new WellFleet();
            fleet.ApplyJson("{\"device\":\"legacy\",\"distance_cm\":10,\"tds_ppm\":100}", null, false, 0, 1000, 20, false, out _);
            fleet.RecalibrateLegacy(40);
            Check(Math.Abs(fleet.Find("legacy").Fill - .75f) < .0001f && fleet.Find("legacy").DepthCm == 10, "changed calibration updates the existing twin without rewriting measured depth");
            fleet.RecalibrateLegacy(5);
            Check(fleet.Find("legacy").ColumnCm == 0, "calibration cannot create negative water height");
            string testDirectory = Path.Combine(Path.GetTempPath(), "AquaTwinSettingsCheck-" + Guid.NewGuid().ToString("N"));
            string testPath = Path.Combine(testDirectory, "settings.json");
            try
            {
                AquaTwinSettingsFileStore.WriteAtomic(testPath, AquaTwinSettingsCodec.EncodeForStorage(original));
                var persisted = AquaTwinSettingsCodec.Decode(File.ReadAllText(testPath));
                Check(persisted.password == original.password, "preferences survive a real file write/read");
                original.lowWaterPercent = 40;
                AquaTwinSettingsFileStore.WriteAtomic(testPath, AquaTwinSettingsCodec.EncodeForStorage(original));
                persisted = AquaTwinSettingsCodec.Decode(File.ReadAllText(testPath));
                Check(persisted.lowWaterPercent == 40, "existing preferences are replaced atomically");
                Check(!File.Exists(testPath + ".tmp"), "successful writes leave no temporary settings file");
            }
            finally
            {
                if (File.Exists(testPath)) File.Delete(testPath);
                if (File.Exists(testPath + ".tmp")) File.Delete(testPath + ".tmp");
                if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory);
            }
#if UNITY_EDITOR
            Debug.Log("AQUATWIN_SETTINGS_CHECKS_PASSED: " + checks);
#else
            Console.WriteLine("AQUATWIN_SETTINGS_CHECKS_PASSED: " + checks);
#endif
        }
    }
}
