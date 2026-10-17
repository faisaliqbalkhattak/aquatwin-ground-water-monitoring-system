using System;
using System.IO;
using UnityEngine;

namespace AquaTwin
{
    public static class AquaTwinMqttLocalSettings
    {
        public const string BrokerHost =
            "6a3cba0faab347c9a1a0c5afcfbd3ca3.s1.eu.hivemq.cloud";
        public const int BrokerPort = 8884;
        public const string WebSocketPath = "/mqtt";
        public const string Topic = "digitaltwin/well01/sensors";
        public const string LegacyFleetTopic = "digitaltwin/+/sensors";
        public const string FleetTopic = "aquatwin/well/+/telemetry";

        private const string BrowserSettingsKey = "AquaTwin.Settings.v1";
        private static AquaTwinMqttSettingsData sessionSettings;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession() { sessionSettings = null; }

        public static string FilePath
        {
            get
            {
#if UNITY_EDITOR
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                return Path.Combine(projectRoot, "UserSettings", "AquaTwinMqttCredentials.json");
#else
                return Path.Combine(Application.persistentDataPath, "AquaTwinSettings.json");
#endif
            }
        }

        public static string StorageDescription
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return "Saved in this browser for this site";
#elif UNITY_EDITOR
                return "Saved for this Unity project";
#else
                return "Saved on this device";
#endif
            }
        }

        public static AquaTwinMqttSettingsData Load()
        {
            if (sessionSettings != null) return sessionSettings.Copy();
            try
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                string json = PlayerPrefs.GetString(BrowserSettingsKey, "");
#else
                string json = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
#endif
                var settings = string.IsNullOrWhiteSpace(json) ? new AquaTwinMqttSettingsData() : AquaTwinSettingsCodec.Decode(json);
                if (!settings.TryValidate(out _, out _)) throw new FormatException("Stored settings contain invalid values.");
                sessionSettings = settings;
            }
            catch
            {
                Debug.LogWarning("[AquaTwin] Could not load saved settings; using defaults.");
                sessionSettings = new AquaTwinMqttSettingsData();
            }
            return sessionSettings.Copy();
        }

        public static void Save(AquaTwinMqttSettingsData settings)
        {
            var validated = settings.Copy();
            validated.Normalize();
            if (!validated.TryValidate(out _, out string error)) throw new ArgumentException(error);
            string json = AquaTwinSettingsCodec.EncodeForStorage(validated);
#if UNITY_WEBGL && !UNITY_EDITOR
            PlayerPrefs.SetString(BrowserSettingsKey, json);
            PlayerPrefs.Save();
#else
            AquaTwinSettingsFileStore.WriteAtomic(FilePath, json);
#endif
            // An unremembered password is available for this session, but is omitted from storage.
            sessionSettings = validated;
        }
    }
}
