using System;
using System.IO;
using UnityEngine;

namespace AquaTwin
{
    public enum AquaTwinDataMode
    {
        Simulation,
        LiveMqtt
    }

    [Serializable]
    public sealed class AquaTwinMqttSettingsData
    {
        public AquaTwinDataMode mode = AquaTwinDataMode.Simulation;
        public string username = "well_dt_esp32";
        public string password = string.Empty;
        public bool applyToDigitalTwin = true;
    }

    public static class AquaTwinMqttLocalSettings
    {
        public const string BrokerHost =
            "6a3cba0faab347c9a1a0c5afcfbd3ca3.s1.eu.hivemq.cloud";
        public const int BrokerPort = 8884;
        public const string WebSocketPath = "/mqtt";
        public const string Topic = "digitaltwin/well01/sensors";

        public static string FilePath
        {
            get
            {
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                    ?? Application.dataPath;
                return Path.Combine(projectRoot, "UserSettings",
                    "AquaTwinMqttCredentials.json");
            }
        }

        public static AquaTwinMqttSettingsData Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new AquaTwinMqttSettingsData();

                AquaTwinMqttSettingsData settings =
                    JsonUtility.FromJson<AquaTwinMqttSettingsData>(File.ReadAllText(FilePath));
                return settings ?? new AquaTwinMqttSettingsData();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[MQTT] Could not read local settings: " + exception.Message);
                return new AquaTwinMqttSettingsData();
            }
        }

#if UNITY_EDITOR
        public static void Save(AquaTwinMqttSettingsData settings)
        {
            string directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonUtility.ToJson(settings, true));
        }
#endif
    }
}
