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
            GetWindow<AquaTwinMqttSetupWindow>(true, "AquaTwin MQTT Setup");
        }

        private void OnEnable()
        {
            settings = AquaTwinMqttLocalSettings.Load();
            minSize = new Vector2(480f, 300f);
        }

        private void OnGUI()
        {
            if (settings == null)
                settings = AquaTwinMqttLocalSettings.Load();

            EditorGUILayout.Space(12f);
            EditorGUILayout.LabelField("HiveMQ Cloud — Well 01", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "The password is saved only in this project's UserSettings folder, " +
                "which is excluded from Git.", MessageType.Info);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Broker", AquaTwinMqttLocalSettings.BrokerHost);
                EditorGUILayout.IntField("Port", AquaTwinMqttLocalSettings.BrokerPort);
                EditorGUILayout.TextField("Topic", AquaTwinMqttLocalSettings.Topic);
            }

            settings.mode = (AquaTwinDataMode)EditorGUILayout.EnumPopup(
                "Data Mode", settings.mode);
            settings.username = EditorGUILayout.TextField("Username", settings.username);
            settings.password = EditorGUILayout.PasswordField("Password", settings.password);
            settings.applyToDigitalTwin = EditorGUILayout.Toggle(
                "Update Digital Twin", settings.applyToDigitalTwin);

            EditorGUILayout.Space(12f);
            if (GUILayout.Button("Save Local Settings", GUILayout.Height(34f)))
            {
                AquaTwinMqttLocalSettings.Save(settings);
                Debug.Log("[MQTT] Local settings saved. Password was not logged or added to Git.");
                ShowNotification(new GUIContent("MQTT settings saved locally"));
            }

            EditorGUILayout.HelpBox(
                "Choose LiveMqtt and press Play. Choose Simulation to restore the " +
                "46-second prerecorded demo.", MessageType.None);
        }
    }
}
