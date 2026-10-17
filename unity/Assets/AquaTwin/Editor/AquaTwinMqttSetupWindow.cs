using UnityEditor;
using UnityEngine;

namespace AquaTwin.Editor
{
    public sealed class AquaTwinMqttSetupWindow : EditorWindow
    {
        private AquaTwinMqttSettingsData settings;

        [MenuItem("AquaTwin/MQTT Connection Setup", priority = 20)]
        private static void Open()
        {
            if (EditorApplication.isPlaying)
            {
                var dashboard = Object.FindFirstObjectByType<FleetDashboard>();
                if (dashboard != null) { dashboard.OpenSettings(); return; }
            }
            GetWindow<AquaTwinMqttSetupWindow>(true, "AquaTwin MQTT Setup");
        }

        private void OnEnable()
        {
            settings = AquaTwinMqttLocalSettings.Load();
            minSize = new Vector2(540f, 560f);
        }

        private void OnGUI()
        {
            if (settings == null)
                settings = AquaTwinMqttLocalSettings.Load();

            EditorGUILayout.Space(12f);
            EditorGUILayout.LabelField("AquaTwin • Well network", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "In Play Mode, use the dashboard's Settings button. " +
                "Preferences are saved locally; storing a password is optional.", MessageType.Info);

            settings.brokerHost = EditorGUILayout.TextField("Broker hostname", settings.brokerHost);
            settings.brokerPort = EditorGUILayout.IntField("WebSocket port", settings.brokerPort);
            settings.webSocketPath = EditorGUILayout.TextField("WebSocket path", settings.webSocketPath);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Fleet topic", AquaTwinMqttLocalSettings.FleetTopic);
                EditorGUILayout.TextField("Legacy topic", AquaTwinMqttLocalSettings.LegacyFleetTopic);
            }

            EditorGUILayout.LabelField("Startup", "Connect to HiveMQ first; demo starts on request");
            settings.username = EditorGUILayout.TextField("Username", settings.username);
            settings.password = EditorGUILayout.PasswordField("Password", settings.password);
            settings.rememberPassword = EditorGUILayout.Toggle("Remember password", settings.rememberPassword);
            if (settings.rememberPassword)
                EditorGUILayout.HelpBox("Remembered passwords are stored locally without encryption.", MessageType.None);
            settings.applyToDigitalTwin = EditorGUILayout.Toggle(
                "Update Digital Twin", settings.applyToDigitalTwin);
            EditorGUILayout.LabelField("Offline demo", "10 wells from Resources/AquaTwinDemo.json");
            settings.legacyWellDepthCm = Mathf.Max(0.1f, EditorGUILayout.FloatField("Legacy calibration (cm)", settings.legacyWellDepthCm));
            settings.staleAfterSeconds = Mathf.Max(3, EditorGUILayout.FloatField("Stale after (seconds)", settings.staleAfterSeconds));
            settings.reviewTdsPpm = Mathf.Max(0, EditorGUILayout.FloatField("TDS review threshold (ppm)", settings.reviewTdsPpm));
            settings.lowWaterPercent = Mathf.Clamp(EditorGUILayout.FloatField("Low water threshold (%)", settings.lowWaterPercent), 0, 100);

            EditorGUILayout.Space(12f);
            settings.Normalize();
            bool valid = settings.TryValidate(out _, out string validationError);
            if (!valid) EditorGUILayout.HelpBox(validationError, MessageType.Warning);
            using (new EditorGUI.DisabledScope(!valid))
            {
                if (GUILayout.Button("Save Local Settings", GUILayout.Height(34f)))
                {
                    try
                    {
                        AquaTwinMqttLocalSettings.Save(settings);
                        ShowNotification(new GUIContent("MQTT settings saved locally"));
                    }
                    catch { ShowNotification(new GUIContent("Could not save settings")); }
                }
            }

            EditorGUILayout.HelpBox(
                "Press Play to check HiveMQ. If disconnected, click Test demo in the dashboard. " +
                "Retry HiveMQ reloads saved credentials without restarting Play Mode. " +
                "Thresholds are operational indicators, not drinking-water certification.", MessageType.None);
        }
    }
}
