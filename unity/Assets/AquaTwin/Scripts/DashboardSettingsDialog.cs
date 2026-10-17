using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace AquaTwin
{
    /// <summary>Runtime UI Toolkit settings dialog, independent of UnityEditor windows.</summary>
    public sealed class DashboardSettingsDialog
    {
        private readonly VisualElement overlay, dialog, connectionPage, monitoringPage;
        private readonly Label error, storage, passwordNote;
        private readonly Button connectionTab, monitoringTab, closeButton, cancelButton, saveButton;
        private readonly TextField host, path, username, password;
        private readonly IntegerField port;
        private readonly Toggle remember, showPassword, applyLive;
        private readonly FloatField calibration, stale, tds, lowWater;
        private readonly Dictionary<string, VisualElement> fields = new();
        private readonly Func<AquaTwinMqttSettingsData, string> save;
        private VisualElement returnFocus;
        private bool connectionActive = true;
        public bool IsOpen => overlay.style.display == DisplayStyle.Flex;

        public DashboardSettingsDialog(VisualElement root, Func<AquaTwinMqttSettingsData, string> save)
        {
            this.save = save;
            overlay = Element(root, "settings-overlay"); overlay.name = "settings-overlay";
            overlay.style.display = DisplayStyle.None;
            overlay.RegisterCallback<PointerDownEvent>(evt => { if (evt.target == overlay) Close(); });
            overlay.RegisterCallback<KeyDownEvent>(HandleKeys, TrickleDown.TrickleDown);
            dialog = Element(overlay, "settings-dialog"); dialog.name = "settings-dialog";
            dialog.focusable = true;
            var header = Element(dialog, "settings-header");
            var title = Element(header, "settings-title-block");
            Label(title, "WORKSPACE PREFERENCES", "settings-eyebrow");
            Label(title, "Dashboard settings", "settings-title");
            Label(title, "Your connection. Your monitoring thresholds.", "settings-subtitle");
            closeButton = Button(header, "×", Close, "settings-close"); closeButton.tooltip = "Close settings without saving";
            var tabs = Element(dialog, "settings-tabs");
            connectionTab = Button(tabs, "Connection", () => ShowPage(true), "settings-tab");
            monitoringTab = Button(tabs, "Monitoring", () => ShowPage(false), "settings-tab");
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("settings-scroll"); dialog.Add(scroll);
            connectionPage = Element(scroll, "settings-page");
            Label(connectionPage, "HIVEMQ CLOUD", "settings-section-label");
            host = TextInput(connectionPage, "Broker hostname", "brokerHost");
            host.tooltip = "Hostname only, without wss:// or a port";
            var endpointRow = Element(connectionPage, "settings-field-row");
            port = new IntegerField("Secure WebSocket port"); Field(endpointRow, port, "brokerPort");
            path = TextInput(endpointRow, "WebSocket path", "webSocketPath");
            Label(connectionPage, "The connection uses encrypted WebSockets (wss).", "settings-hint");
            username = TextInput(connectionPage, "Username", "username");
            password = TextInput(connectionPage, "Password", "password"); password.isPasswordField = true;
            var passwordOptions = Element(connectionPage, "settings-password-options");
            showPassword = new Toggle("Show password"); showPassword.AddToClassList("settings-toggle"); passwordOptions.Add(showPassword);
            showPassword.RegisterValueChangedCallback(evt => password.isPasswordField = !evt.newValue);
            remember = new Toggle("Remember password on this device"); remember.AddToClassList("settings-toggle"); passwordOptions.Add(remember);
            passwordNote = Label(connectionPage, "", "settings-hint");
            remember.RegisterValueChangedCallback(_ => UpdatePasswordNote());
            var stream = Element(connectionPage, "settings-stream-info");
            Label(stream, "WELL STREAMS", "settings-section-label");
            Label(stream, AquaTwinMqttLocalSettings.FleetTopic, "settings-topic");
            Label(stream, AquaTwinMqttLocalSettings.LegacyFleetTopic, "settings-topic");
            Label(stream, "Wells are discovered automatically from these telemetry contracts.", "settings-hint");

            monitoringPage = Element(scroll, "settings-page");
            Label(monitoringPage, "LIVE TELEMETRY", "settings-section-label");
            applyLive = new Toggle("Update the digital twin from live readings");
            applyLive.AddToClassList("settings-toggle"); monitoringPage.Add(applyLive);
            Label(monitoringPage, "Pause incoming live updates while keeping the local demo available.", "settings-hint");
            var timing = Element(monitoringPage, "settings-field-row");
            calibration = FloatInput(timing, "Legacy well depth (cm)", "legacyWellDepthCm");
            stale = FloatInput(timing, "Stale after (seconds)", "staleAfterSeconds");
            Label(monitoringPage, "Calibration applies to legacy distance readings. Canonical wells use their reported dimensions.", "settings-hint");
            Label(monitoringPage, "REVIEW THRESHOLDS", "settings-section-label");
            var thresholds = Element(monitoringPage, "settings-field-row");
            tds = FloatInput(thresholds, "TDS above (ppm)", "reviewTdsPpm");
            lowWater = FloatInput(thresholds, "Water column below (%)", "lowWaterPercent");
            Label(monitoringPage, "These are operational review indicators, not water-safety certification.", "settings-hint");
            var demo = Element(monitoringPage, "settings-stream-info");
            Label(demo, "OFFLINE WORKSPACE", "settings-section-label");
            Label(demo, "10 example wells · 90-second replay", "settings-topic");
            Label(demo, "Use Test demo in the dashboard. Settings changes preserve the active demo and its history.", "settings-hint");

            error = Label(dialog, "", "settings-error"); error.name = "settings-error";
            error.style.display = DisplayStyle.None;
            var footer = Element(dialog, "settings-footer");
            var footerCopy = Element(footer, "settings-footer-copy");
            storage = Label(footerCopy, "", "settings-storage");
            Label(footerCopy, "Changes take effect when you save.", "settings-hint");
            var actions = Element(footer, "settings-actions");
            cancelButton = Button(actions, "Cancel", Close, "settings-cancel");
            saveButton = Button(actions, "Save & apply", Save, "settings-save");
            ShowPage(true);
        }

        public void Open(AquaTwinMqttSettingsData settings, VisualElement opener)
        {
            returnFocus = opener;
            host.SetValueWithoutNotify(settings.brokerHost); port.SetValueWithoutNotify(settings.brokerPort);
            path.SetValueWithoutNotify(settings.webSocketPath); username.SetValueWithoutNotify(settings.username);
            password.SetValueWithoutNotify(settings.password); password.isPasswordField = true;
            showPassword.SetValueWithoutNotify(false); remember.SetValueWithoutNotify(settings.rememberPassword);
            applyLive.SetValueWithoutNotify(settings.applyToDigitalTwin);
            calibration.SetValueWithoutNotify(settings.legacyWellDepthCm); stale.SetValueWithoutNotify(settings.staleAfterSeconds);
            tds.SetValueWithoutNotify(settings.reviewTdsPpm); lowWater.SetValueWithoutNotify(settings.lowWaterPercent);
            storage.text = AquaTwinMqttLocalSettings.StorageDescription;
            UpdatePasswordNote(); ClearErrors(); ShowPage(true);
            overlay.style.display = DisplayStyle.Flex;
            host.schedule.Execute(() => { if (IsOpen) host.Focus(); });
        }
        public void Close()
        {
            overlay.style.display = DisplayStyle.None;
            password.SetValueWithoutNotify("");
            returnFocus?.Focus();
        }
        private void Save()
        {
            ClearErrors();
            var draft = new AquaTwinMqttSettingsData
            {
                brokerHost = host.value, brokerPort = port.value, webSocketPath = path.value,
                username = username.value, password = password.value, rememberPassword = remember.value,
                applyToDigitalTwin = applyLive.value, legacyWellDepthCm = calibration.value,
                staleAfterSeconds = stale.value, reviewTdsPpm = tds.value, lowWaterPercent = lowWater.value
            };
            draft.Normalize();
            if (!draft.TryValidate(out string field, out string message))
            {
                ShowPage(field == "brokerHost" || field == "brokerPort" || field == "webSocketPath" || field == "username" || field == "password");
                fields[field].AddToClassList("invalid"); fields[field].Focus(); ShowError(message); return;
            }
            message = save(draft);
            if (message != null) { ShowError(message); return; }
            Close();
        }
        private void ShowError(string text) { error.text = text; error.style.display = DisplayStyle.Flex; }
        private void ClearErrors()
        {
            error.style.display = DisplayStyle.None;
            foreach (var field in fields.Values) field.RemoveFromClassList("invalid");
        }
        private void ShowPage(bool connection)
        {
            connectionActive = connection;
            connectionPage.style.display = connection ? DisplayStyle.Flex : DisplayStyle.None;
            monitoringPage.style.display = connection ? DisplayStyle.None : DisplayStyle.Flex;
            connectionTab.EnableInClassList("active", connection); monitoringTab.EnableInClassList("active", !connection);
        }
        private void UpdatePasswordNote()
        {
            passwordNote.text = remember.value
                ? "Remembered passwords are stored locally without encryption."
                : "The password is used for this session only; other preferences are always saved.";
        }
        private void HandleKeys(KeyDownEvent evt)
        {
            if (!IsOpen) return;
            if (evt.keyCode == KeyCode.Escape) { Close(); evt.StopImmediatePropagation(); overlay.panel?.focusController?.IgnoreEvent(evt); return; }
            if (evt.keyCode != KeyCode.Tab) return;
            var order = new List<VisualElement> { closeButton, connectionTab, monitoringTab };
            if (connectionActive) order.AddRange(new VisualElement[] { host, port, path, username, password, showPassword, remember });
            else order.AddRange(new VisualElement[] { applyLive, calibration, stale, tds, lowWater });
            order.Add(cancelButton); order.Add(saveButton);
            var focused = overlay.panel?.focusController?.focusedElement as VisualElement;
            int index = order.FindIndex(item => item == focused || focused != null && item.Contains(focused));
            int next = (index + (evt.shiftKey ? -1 : 1) + order.Count) % order.Count;
            order[next].Focus(); evt.StopImmediatePropagation(); overlay.panel?.focusController?.IgnoreEvent(evt);
        }
        private void Field(VisualElement parent, VisualElement element, string name)
        {
            element.name = "setting-" + name; element.AddToClassList("settings-field"); fields.Add(name, element); parent.Add(element);
        }
        private TextField TextInput(VisualElement parent, string title, string key)
        { var input = new TextField(title); Field(parent, input, key); return input; }
        private FloatField FloatInput(VisualElement parent, string title, string key)
        { var input = new FloatField(title); Field(parent, input, key); return input; }
        private static VisualElement Element(VisualElement parent, string css)
        { var item = new VisualElement(); item.AddToClassList(css); parent.Add(item); return item; }
        private static Label Label(VisualElement parent, string title, string css)
        { var item = new Label(title) { enableRichText = false }; item.AddToClassList(css); parent.Add(item); return item; }
        private static Button Button(VisualElement parent, string title, System.Action click, string css)
        { var item = new Button(click) { text = title }; item.AddToClassList(css); parent.Add(item); return item; }

        public static Button CreateSettingsButton(System.Action click)
        {
            var button = new Button(click) { name = "dashboard-settings", tooltip = "Open dashboard settings" };
            button.AddToClassList("settings-launch");
            button.Add(new GearIcon());
            Label(button, "SETTINGS", "settings-launch-text");
            return button;
        }
        private sealed class GearIcon : VisualElement
        {
            public GearIcon() { AddToClassList("settings-gear"); pickingMode = PickingMode.Ignore; generateVisualContent += Draw; }
            private void Draw(MeshGenerationContext context)
            {
                var painter = context.painter2D;
                Vector2 center = contentRect.center;
                float radius = Mathf.Min(contentRect.width, contentRect.height) * .43f;
                painter.strokeColor = new Color(.57f, .88f, .81f); painter.lineWidth = 1.3f;
                painter.BeginPath();
                for (int i = 0; i < 32; i++)
                {
                    float angle = i * Mathf.PI * 2 / 32;
                    float r = radius * (i % 4 < 2 ? 1 : .77f);
                    Vector2 point = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r;
                    if (i == 0) painter.MoveTo(point); else painter.LineTo(point);
                }
                painter.ClosePath(); painter.Stroke(); painter.BeginPath();
                for (int i = 0; i < 24; i++)
                {
                    float angle = i * Mathf.PI * 2 / 24;
                    Vector2 point = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius * .35f;
                    if (i == 0) painter.MoveTo(point); else painter.LineTo(point);
                }
                painter.ClosePath(); painter.Stroke();
            }
        }
    }
}
