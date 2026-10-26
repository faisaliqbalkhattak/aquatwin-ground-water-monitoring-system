using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AquaTwin
{
    [DefaultExecutionOrder(-1100)]
    public sealed class FleetDashboard : MonoBehaviour
    {
        private static FleetDashboard instance;
        private readonly FleetSession session = new();
        public WellFleet Fleet => session.ActiveFleet;
        private AquaTwinMqttSettingsData settings;
        private FleetScene scene;
        private UIDocument document;
        private PanelSettings panel;
        private VisualElement root, viewport, markers, empty, detailBody;
        private Image viewportImage;
        private ListView list;
        private TextField search;
        private DropdownField filter;
        private readonly List<WellFleet.Well> filtered = new();
        private readonly Dictionary<string, Button> pins = new();
        private readonly Dictionary<string, Label> labels = new();
        private VisualElement levelFill;
        private Trend trend;
        private string selected, hovered;
        private string transport => session.Connection;
        private DemoTelemetryDataset demoDataset;
        private VisualElement demoControls;
        private Button demoButton, pauseButton;
        private IntegerField demoCountInput;
        private bool demoPaused;
        private float demoPlayhead, lastDemoClock;
        private bool IsDemo => session.IsDemo;
        private float nextRefresh, nextDemo;
        private int knownCount = -1, demoCount;
        private bool dragging;
        private Vector2 pointer;
        private Vector2 pointerStart;
        private DashboardSettingsDialog settingsDialog;
        private GuardianFeed guardian;
        private GuardianPanel guardianPanel;
        private bool editorPreview;
        private bool twinMaximized;
        private Button maximizeButton;
        private RenderTexture previewRenderedTexture;
        private readonly Dictionary<Camera,bool> previewCameras = new();
        private Button settingsButton;
        private Label settingsToast;
        private VisualElement connectionDiagnostics;
        private float toastUntil;
        public string SelectedId => selected;
        public string Transport => transport;
        public void OpenSettings() { settingsDialog.Open(AquaTwinMqttLocalSettings.Load(), settingsButton); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic() { instance = null; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install() { EnsureInstance(); }

        public static FleetDashboard EnsureInstance()
        {
            if (instance != null) return instance;
            instance = FindFirstObjectByType<FleetDashboard>();
            if (instance == null) instance = new GameObject("AquaTwin Well Network").AddComponent<FleetDashboard>();
            return instance;
        }

        private void Awake() { if(Application.isPlaying) InitializeDashboard(); }
        private void OnEnable()
        {
            if(!Application.isPlaying)return;
            // Unity calls OnEnable, but not Awake, after an Editor assembly reload.
            // UI Toolkit controls and managed lookup dictionaries are not serialized.
            if (root != null && labels.ContainsKey("discovered")) return;
            ClearRuntimeGraph();
            InitializeDashboard();
        }
        private void ClearRuntimeGraph()
        {
            var resources = new HashSet<UnityEngine.Object>();
            foreach (var old in GetComponents<UIDocument>())
            {
                if (old.panelSettings != null) resources.Add(old.panelSettings);
                DestroyImmediate(old);
            }
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.sharedMaterials) if (material != null) resources.Add(material);
            foreach (var mesh in GetComponentsInChildren<MeshFilter>(true))
                if (mesh.sharedMesh != null && mesh.sharedMesh.name == "Schematic water envelope") resources.Add(mesh.sharedMesh);
            foreach (var camera in GetComponentsInChildren<Camera>(true))
                if (camera.targetTexture != null) resources.Add(camera.targetTexture);
            foreach (var old in GetComponents<FleetScene>()) DestroyImmediate(old);
            // This GameObject and its children belong exclusively to the runtime dashboard.
            for (int i = transform.childCount - 1; i >= 0; i--) DestroyImmediate(transform.GetChild(i).gameObject);
            foreach (var resource in resources) if (resource != null) DestroyImmediate(resource);
            labels.Clear(); pins.Clear(); filtered.Clear();
            selected = null; knownCount = -1; twinMaximized=false;
            root = null; scene = null; document = null;
        }
#if UNITY_EDITOR
        public void InitializeEditorPreview()
        {
            if(Application.isPlaying)return;
            if(root==null || !labels.ContainsKey("discovered"))
            {
                editorPreview=true;
                foreach(var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))previewCameras[camera]=camera.enabled;
                ClearRuntimeGraph();InitializeDashboard();
                session.SetConnection("PREVIEW","Editor preview · press Play to connect to HiveMQ");
            }
            Refresh();ResizeViewport();
            if(scene.Texture!=previewRenderedTexture){scene.Camera.Render();previewRenderedTexture=scene.Texture;}
            foreach(var component in GetComponents<Component>())if(component is FleetScene||component is UIDocument||component is GuardianFeed)component.hideFlags=HideFlags.DontSave;
            foreach(var child in GetComponentsInChildren<Transform>(true))if(child!=transform)child.gameObject.hideFlags=HideFlags.DontSave;
        }
        public void ClearEditorPreview()
        {
            if(!editorPreview)return;
            ClearRuntimeGraph();
            foreach(var pair in previewCameras)if(pair.Key!=null)pair.Key.enabled=pair.Value;
            previewCameras.Clear();editorPreview=false;previewRenderedTexture=null;
        }
#endif
        private void InitializeDashboard()
        {
            instance = this;
            settings = AquaTwinMqttLocalSettings.Load();
            settings.staleAfterSeconds = Mathf.Max(3, settings.staleAfterSeconds);
            settings.legacyWellDepthCm = Mathf.Max(.1f, settings.legacyWellDepthCm);
            demoCount = 10;
            // The authored scene now contains only the network dashboard. Legacy assets
            // remain available in the repository; no legacy overlay is activated.
            guardian = GuardianFeed.Ensure(gameObject);
            scene = gameObject.AddComponent<FleetScene>();
            scene.Initialize();
            scene.ResolveInsight = id => guardian.Find(id,IsDemo);
            BuildInterface();
            Refresh();
        }

        public bool ApplyTelemetry(string json, string topic, bool retained, out string error)
        {
            if (topic != null && !MqttTopics.AnyMatch(settings.SubscriptionTopics, topic))
            { error = "Message topic is outside the configured subscriptions."; return false; }
            return session.LiveFleet.ApplyJson(json, topic, retained, Time.unscaledTime,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                settings.legacyWellDepthCm, false, out error, subscribedTopic: true);
        }
        public void SetTransportState(string state, string detail = null)
        {
            if (session.Connection == state && session.ConnectionDetail == detail) return;
            session.SetConnection(state, detail); nextRefresh = 0;
        }
        public void SetTelemetryDelivery(int messagesThisConnection, int rejected) { session.SetDeliveryCounts(messagesThisConnection, rejected); }
        public int RestoreLastKnown(FleetSnapshotCache cache, string json)
        {
            int count = cache.Restore(json, session.LiveFleet, settings.SubscriptionTopics, Time.unscaledTime,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, settings.legacyWellDepthCm);
            nextRefresh = 0; return count;
        }
        public void ResetLiveSource()
        {
            session.LiveFleet.Clear(); if (!IsDemo) { selected = null; knownCount = -1; }
            nextRefresh = 0;
        }
        public void SetConnectionDiagnostics(string endpoint, string topics, string counts, string error, string activity, string history)
        {
            if (!labels.ContainsKey("mqttEndpoint")) return;
            Set("mqttEndpoint", "ENDPOINT  " + endpoint);
            Set("mqttTopics", "SUBSCRIPTIONS  " + topics);
            Set("mqttCounts", counts);
            Set("mqttActivity", activity);
            Set("mqttError", string.IsNullOrEmpty(error) ? "No connection or payload errors recorded." : "LAST ISSUE  " + error);
            labels["mqttError"].EnableInClassList("has-error", !string.IsNullOrEmpty(error));
            Set("mqttHistory", history);
        }
        public void StartDemo()
        {
            try
            {
                if (demoDataset == null)
                {
                    TextAsset asset = Resources.Load<TextAsset>("AquaTwinDemo");
                    if (asset == null) throw new InvalidOperationException("AquaTwinDemo.json was not found in Resources.");
                    demoDataset = DemoTelemetryDataset.Load(asset.text);
                    demoCount = demoDataset.WellCount;
                    demoCountInput.SetValueWithoutNotify(demoCount);
                }
                session.StartDemo();
                selected = null; knownCount = -1;
                demoPlayhead = 0; demoPaused = false;
                lastDemoClock = Time.unscaledTime; nextDemo = lastDemoClock;
                pauseButton.text = "Pause";
                search.SetValueWithoutNotify(""); filter.SetValueWithoutNotify("All wells");
                GenerateDemo(); nextDemo = Time.unscaledTime + demoDataset.SampleIntervalSeconds;
                Set("demoError", "");
                Refresh();
            }
            catch (Exception exception)
            {
                session.ReturnToLive();
                Set("demoError", "Demo could not load: " + exception.Message);
                Debug.LogError("[AquaTwin demo] " + exception.Message);
                nextRefresh = 0;
            }
        }
        public void ReturnToLive()
        {
            session.ReturnToLive(); selected = null; knownCount = -1;
            search.SetValueWithoutNotify(""); filter.SetValueWithoutNotify("All wells"); Refresh();
        }
        private void RetryHiveMqtt()
        {
            var client = GetComponent<HiveMqttWellDataSource>();
            if (client != null) client.RetryConnection();
            else gameObject.AddComponent<HiveMqttWellDataSource>();
        }
        private string SaveDashboardSettings(AquaTwinMqttSettingsData draft)
        {
            bool reconnect = !settings.SameConnection(draft);
            try { AquaTwinMqttLocalSettings.Save(draft); }
            catch { return "Settings could not be saved. Check available local storage and try again."; }
            settings = AquaTwinMqttLocalSettings.Load();
            session.LiveFleet.RecalibrateLegacy(settings.legacyWellDepthCm);
            var client = GetComponent<HiveMqttWellDataSource>();
            if (client != null) client.ApplySettings(settings, reconnect);
            else if (reconnect) gameObject.AddComponent<HiveMqttWellDataSource>();
            settingsToast.text = reconnect ? "Settings saved · reconnecting to HiveMQ" : "Settings saved · monitoring preferences applied";
            settingsToast.style.display = DisplayStyle.Flex;
            toastUntil = Time.unscaledTime + 4;
            nextRefresh = 0;
            return null;
        }
        public void SelectWell(string id)
        {
            if (Fleet.Find(id) == null) return;
            selected = id;
            nextRefresh = 0;
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            if (settingsToast != null && now > toastUntil) settingsToast.style.display = DisplayStyle.None;
            if (IsDemo && !demoPaused)
            {
                demoPlayhead += now - lastDemoClock;
                if (demoPlayhead >= demoDataset.DurationSeconds)
                {
                    demoPlayhead %= demoDataset.DurationSeconds;
                    session.DemoFleet.Clear(); selected = null; knownCount = -1; nextDemo = 0;
                }
                if (now >= nextDemo) { GenerateDemo(); nextDemo = now + demoDataset.SampleIntervalSeconds; }
            }
            lastDemoClock = now;
            guardianPanel?.Tick();
            if (Time.unscaledTime >= nextRefresh) { Refresh(); nextRefresh = Time.unscaledTime + .5f; }
        }

        private void BuildInterface()
        {
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1440, 900);
            panel.match = .5f;
            panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("FleetTheme");
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.sortingOrder = 100;
            root = document.rootVisualElement;
            root.AddToClassList("app");
            root.style.unityFontDefinition = FontDefinition.FromFont(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            root.styleSheets.Add(Resources.Load<StyleSheet>("FleetDashboard"));
            root.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                root.EnableInClassList("compact", evt.newRect.width < 1200);
                root.EnableInClassList("narrow", evt.newRect.width < 850);
            });

            var header = Box(root, "header");
            var brand = Box(header, "brand");
            Text(brand, "◈", "brand-mark");
            var brandCopy = Box(brand, "brand-copy");
            Text(brandCopy, "AQUATWIN", "brand-name");
            Text(brandCopy, "GROUNDWATER INTELLIGENCE", "eyebrow");
            var title = Box(header, "header-title");
            Text(title, "Network overview", "page-title");
            Text(title, "Observe the invisible.", "muted");
            var headerRight = Box(header, "header-right");
            var headerMeta = Box(headerRight, "header-meta");
            KeyText(headerMeta, "connection", "", "connection");
            settingsButton = DashboardSettingsDialog.CreateSettingsButton(OpenSettings);
            headerMeta.Add(settingsButton);
            KeyText(headerRight, "liveDataStatus", "NO SENSOR DATA RECEIVED", "data-status");
            KeyText(headerRight, "connectionDetail", "", "connection-detail");
            var connectionActions = Box(headerRight, "connection-actions");
            demoButton = new Button(StartDemo) { text = "Test demo", name = "test-demo", tooltip = "Replay the local ten-well JSON dataset" };
            demoButton.AddToClassList("tool"); demoButton.AddToClassList("demo-launch");
            connectionActions.Add(demoButton);
            Action(connectionActions, "Insights", () => guardianPanel.Open(), "Trends, forecasts, scenarios and historical context");
            Action(connectionActions, "Connection details", () =>
                connectionDiagnostics.style.display = connectionDiagnostics.style.display == DisplayStyle.Flex ? DisplayStyle.None : DisplayStyle.Flex,
                "Inspect endpoint, subscriptions, message counts and recent connection events");
            KeyText(headerRight, "demoError", "", "demo-error");
            connectionDiagnostics = Box(root, "mqtt-diagnostics");
            connectionDiagnostics.style.display = DisplayStyle.None;
            var diagnosticHead = Box(connectionDiagnostics, "section-head");
            Text(diagnosticHead, "HiveMQ connection", "section-title");
            Action(diagnosticHead, "Retry HiveMQ", RetryHiveMqtt, "Reconnect with saved settings");
            Action(diagnosticHead, "Close", () => connectionDiagnostics.style.display = DisplayStyle.None, "Close connection details");
            var diagnosticScroll = new ScrollView(ScrollViewMode.Vertical); connectionDiagnostics.Add(diagnosticScroll);
            KeyText(diagnosticScroll, "mqttEndpoint", "", "mqtt-detail");
            KeyText(diagnosticScroll, "mqttTopics", "", "mqtt-detail");
            KeyText(diagnosticScroll, "mqttCounts", "", "mqtt-detail");
            KeyText(diagnosticScroll, "mqttActivity", "", "mqtt-detail");
            KeyText(diagnosticScroll, "mqttError", "", "mqtt-error");
            KeyText(diagnosticScroll, "mqttHistory", "", "mqtt-history");
            demoControls = Box(root, "demo-controls");
            Text(demoControls, "DEMO DATA · LOCAL JSON", "demo-badge");
            KeyText(demoControls, "demoTime", "", "demo-time");
            demoCountInput = new IntegerField("Wells") { value = demoCount, isDelayed = true };
            demoCountInput.AddToClassList("demo-input");
            demoCountInput.RegisterValueChangedCallback(evt =>
            {
                demoCount = Mathf.Clamp(evt.newValue, 0, demoDataset?.WellCount ?? 10);
                demoCountInput.SetValueWithoutNotify(demoCount);
                if (IsDemo) StartDemo();
            });
            demoControls.Add(demoCountInput);
            pauseButton = new Button(() => { demoPaused = !demoPaused; pauseButton.text = demoPaused ? "Resume" : "Pause"; nextRefresh = 0; }) { text = "Pause" };
            pauseButton.AddToClassList("tool"); demoControls.Add(pauseButton);
            Action(demoControls, "Restart demo", StartDemo, "Restart the JSON sequence and clear demo history");
            Action(demoControls, "Return to live", ReturnToLive, "Show only real HiveMQ readings");
            Text(demoControls, "90s loop · samples every 2s · pause lets readings become stale", "demo-help");

            var summary = Box(root, "summary");
            Metric(summary, "discovered", "DISCOVERED WELLS", "0", "Live and last-known well identities");
            Metric(summary, "reporting", "REPORTING NOW", "0", "Fresh telemetry, per well");
            Metric(summary, "attention", "NEEDS ATTENTION", "0", "Fresh wells outside set thresholds");
            Metric(summary, "fill", "MEAN WATER COLUMN", "—", "Unweighted mean · reporting wells", true);

            var workspace = Box(root, "workspace");
            var sidebar = Box(workspace, "sidebar");
            var sideHead = Box(sidebar, "section-head");
            Text(sideHead, "Well network", "section-title");
            KeyText(sideHead, "listCount", "0", "count");
            search = new TextField("Search well ID");
            search.AddToClassList("search");
            search.RegisterValueChangedCallback(_ => RefreshList());
            sidebar.Add(search);
            filter = new DropdownField(new List<string> { "All wells", "Reporting", "Attention", "Stale / offline" }, 0);
            filter.AddToClassList("filter");
            filter.RegisterValueChangedCallback(_ => RefreshList());
            sidebar.Add(filter);
            list = new ListView(filtered, 88, MakeRow, BindRow) { selectionType = SelectionType.None, virtualizationMethod = CollectionVirtualizationMethod.FixedHeight };
            list.AddToClassList("well-list");
            sidebar.Add(list);
            KeyText(sidebar, "listEmpty", "", "list-empty");
            sidebar.tooltip = "New IDs appear automatically; stale wells remain available.";

            var center = Box(workspace, "center");
            var toolbar = Box(center, "view-toolbar");
            var viewTitle = Box(toolbar, "view-title");
            Text(viewTitle, "Subsurface twin", "section-title");
            KeyText(viewTitle, "layout", "SCHEMATIC LAYOUT", "eyebrow");
            var tools = Box(toolbar, "tools");
            var layer = new DropdownField(new List<string> { "Water level", "TDS patterns", "Pumping impact" }, 0);
            layer.AddToClassList("layer-select"); tools.Add(layer);
            layer.RegisterValueChangedCallback(_ => { scene.Layer = layer.index; nextRefresh = 0; });
            ToggleButton(tools, "Geology", () => { scene.ShowGeology = !scene.ShowGeology; return scene.ShowGeology; });
            ToggleButton(tools, "Water envelope", () => { scene.ShowSurface = !scene.ShowSurface; return scene.ShowSurface; });
            Action(tools, "−", () => scene.Zoom(2), "Zoom out");
            Action(tools, "+", () => scene.Zoom(-2), "Zoom in");
            Action(tools, "Reset view", () => scene.ResetView(), "Fit the entire network");
            maximizeButton = new Button(() => SetTwinMaximized(!twinMaximized)) { text = "Maximize" };
            maximizeButton.AddToClassList("tool"); maximizeButton.tooltip="Expand the entire twin; Escape restores the dashboard"; tools.Add(maximizeButton);
            root.focusable=true;
            root.RegisterCallback<KeyDownEvent>(evt => { if(evt.keyCode==KeyCode.Escape && twinMaximized){SetTwinMaximized(false);evt.StopPropagation();} });
            viewport = Box(center, "viewport");
            viewportImage = new Image { image = scene.Texture, scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
            viewportImage.AddToClassList("viewport-image");
            viewport.Add(viewportImage);
            markers = Box(viewport, "markers"); markers.pickingMode = PickingMode.Ignore;
            empty = Box(viewport, "empty-state"); empty.pickingMode = PickingMode.Ignore;
            Text(empty, "◈", "empty-icon");
            KeyText(empty, "emptyTitle", "Waiting for your wells", "empty-title");
            KeyText(empty, "emptyMessage", "Each new sensor identity becomes a digital twin.\nConnect HiveMQ to start discovering your network.", "empty-message");
            var corner = Box(viewport, "view-corner"); corner.pickingMode = PickingMode.Ignore;
            Text(corner, "3D / GEOLOGICAL SECTION", "eyebrow");
            Text(corner, "Drag to orbit · scroll to zoom · select a well", "muted");
            viewport.RegisterCallback<GeometryChangedEvent>(_ => ResizeViewport());
            viewport.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || evt.target is Button) return;
                dragging = true; pointer = evt.position; pointerStart = pointer;
                viewport.CapturePointer(evt.pointerId);
            });
            viewport.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!dragging)
                {
                    Vector2 local = viewport.WorldToLocal(evt.position);
                    string id=scene.Pick(new Vector2(local.x/Mathf.Max(1,viewport.resolvedStyle.width),1-local.y/Mathf.Max(1,viewport.resolvedStyle.height)));
                    if(id!=hovered){hovered=id;PlaceMarkers();}
                    return;
                }
                Vector2 position = evt.position;
                scene.Orbit(position - pointer); pointer = position; PlaceMarkers();
            });
            viewport.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (dragging && Vector2.Distance(pointerStart, evt.position) < 5)
                {
                    Vector2 local = viewport.WorldToLocal(evt.position);
                    string id = scene.Pick(new Vector2(local.x / viewport.resolvedStyle.width, 1 - local.y / viewport.resolvedStyle.height));
                    if (id != null) SelectWell(id);
                }
                dragging = false; viewport.ReleasePointer(evt.pointerId);
            });
            viewport.RegisterCallback<PointerCaptureOutEvent>(_ => dragging = false);
            viewport.RegisterCallback<PointerLeaveEvent>(_ => { hovered=null;PlaceMarkers(); });
            viewport.RegisterCallback<WheelEvent>(evt => { scene.Zoom(evt.delta.y); PlaceMarkers(); evt.StopPropagation(); });
            var legend = Box(center, "view-legend");
            Text(legend, "●  Water column", "legend-water");
            Text(legend, "●  Elevated TDS", "legend-warning");
            Text(legend, "●  Stale / offline", "muted");
            Text(legend, "Relative fill · schematic depth", "muted");
            KeyText(center, "envelopeNote", "", "view-note");

            var inspector = Box(workspace, "inspector");
            var detailHeader = Box(inspector, "section-head");
            Text(detailHeader, "Well details", "section-title");
            var step = Box(detailHeader, "tools");
            Action(step, "‹", () => StepSelection(-1), "Previous well");
            Action(step, "›", () => StepSelection(1), "Next well");
            KeyText(inspector, "selectedId", "Select a well", "well-title");
            KeyText(inspector, "selectedStatus", "No telemetry received", "well-status");
            var detailScroll = new ScrollView(ScrollViewMode.Vertical); detailScroll.AddToClassList("detail-scroll"); inspector.Add(detailScroll);
            detailBody = Box(detailScroll, "detail-body");
            Text(detailBody, "WATER COLUMN", "eyebrow");
            KeyText(detailBody, "selectedFill", "—", "fill-value");
            var track = Box(detailBody, "level-track"); levelFill = Box(track, "level-fill");
            DetailMetric("depth", "Depth to water", "cm below sensor");
            DetailMetric("tds", "Dissolved solids", "ppm · estimated TDS");
            KeyText(detailBody, "tdsNote", "", "metric-note");
            var intelligence=Box(detailBody,"guardian-summary");
            Text(intelligence,"GUARDIAN","eyebrow");
            KeyText(intelligence,"guardianSummary","Collecting evidence","guardian-summary-text");
            Action(intelligence,"Open insights",()=>guardianPanel.Open(),"Investigate trends and test scenarios");
            var more = new Foldout { text="History & sensor details", value=false }; more.AddToClassList("more-details");detailBody.Add(more);
            var primary=detailBody;detailBody=more.contentContainer;
            KeyText(detailBody,"calibration","","muted");
            DetailMetric("column","Water column height","cm above well bottom");
            DetailMetric("ph","pH","Optional sensor channel");
            var trendHeader=Box(detailBody,"trend-header");Text(trendHeader,"Depth history","section-title");KeyText(trendHeader,"trendCount","0 / 120","eyebrow");
            KeyText(detailBody,"trendRange","Waiting for samples","muted");trend=new Trend();trend.AddToClassList("trend");detailBody.Add(trend);
            KeyText(detailBody,"trendNote","History builds from incoming readings.","muted");
            KeyText(detailBody,"location","","location");KeyText(detailBody,"updated","","muted");
            Text(detailBody,"Operational review thresholds do not certify water quality. Column fill is not aquifer storage.","inspector-note");
            detailBody=primary;
            var footer = Box(root, "footer");
            KeyText(footer, "source", "", "eyebrow");
            Text(footer, "AQUATWIN  /  A LIVING VIEW BELOW THE SURFACE", "eyebrow");
            settingsToast = Text(root, "", "settings-toast");
            settingsToast.style.display = DisplayStyle.None;
            settingsToast.pickingMode = PickingMode.Ignore;
            settingsDialog = new DashboardSettingsDialog(root, SaveDashboardSettings);
            guardianPanel = new GuardianPanel(root, guardian, scene, () => Fleet.Find(selected), () => IsDemo);
        }

        private void Refresh()
        {
            if (selected == null || Fleet.Find(selected) == null) selected = Fleet.Wells.FirstOrDefault()?.Id;
            float now = Time.unscaledTime;
            var fresh = Fleet.Wells.Where(w => w.IsFresh(now, settings.staleAfterSeconds)).ToList();
            Set("discovered", Fleet.Wells.Count.ToString());
            Set("reporting", fresh.Count.ToString());
            Set("attention", fresh.Count(w => NeedsReview(w)).ToString());
            Set("fill", fresh.Count == 0 ? "—" : F(fresh.Average(w => w.Fill) * 100, "0.0") + "%");
            bool connected = transport == "CONNECTED";
            bool progressing = transport == "CONNECTING" || transport == "AUTHENTICATING" || transport == "SUBSCRIBING";
            string state = connected ? "●  HIVEMQ / BROKER CONNECTED" : progressing ? "●  HIVEMQ / " + transport : "●  HIVEMQ / OFFLINE · DISCONNECTED";
            if(editorPreview)state="EDITOR PREVIEW · PRESS PLAY";
            else state=state.Replace("HIVEMQ / BROKER CONNECTED","HiveMQ connected").Replace("HIVEMQ / ","HiveMQ ");
            Set("connection", state);
            labels["connection"].EnableInClassList("offline", transport == "OFFLINE");
            labels["connection"].EnableInClassList("connecting", progressing);
            labels["connection"].EnableInClassList("no-telemetry", connected && session.FreshLiveCount(now, settings.staleAfterSeconds) == 0);
            Set("connectionDetail", session.ConnectionDetail.Length > 95 ? session.ConnectionDetail.Substring(0,92)+"…" : session.ConnectionDetail);
            labels["connectionDetail"].tooltip = session.ConnectionDetail;
            Set("liveDataStatus", editorPreview ? "Monitoring starts in Play Mode" : session.LiveDataStatus(now, settings.staleAfterSeconds)
                .Replace("LAST KNOWN READINGS · WAITING FOR LIVE DATA","LAST KNOWN · WAITING FOR DATA")
                .Replace("BROKER CONNECTED · NO SENSOR MESSAGES","WAITING FOR SENSOR DATA"));
            labels["liveDataStatus"].EnableInClassList("no-data", session.FreshLiveCount(now, settings.staleAfterSeconds) == 0);
            // A broker can be reachable without any sensors, or contain only stale/retained
            // readings. Demo must remain available, even while the broker is connecting.
            demoButton.style.display = session.DemoAvailable ? DisplayStyle.Flex : DisplayStyle.None;
            demoControls.style.display = IsDemo ? DisplayStyle.Flex : DisplayStyle.None;
            if (IsDemo) Set("demoTime", F(demoPlayhead, "0") + " / " + F(demoDataset.DurationSeconds, "0") + "s" + (demoPaused ? " · PAUSED" : " · REPLAYING"));
            Set("source", IsDemo ? "DEMO DATA  /  LOCAL AQUATWINDEMO.JSON  /  SIMULATED SENSOR READINGS" : settings.applyToDigitalTwin ? "HIVEMQ CLOUD  /  AUTO-DISCOVERY ENABLED" : "HIVEMQ CLOUD  /  TWIN UPDATES PAUSED IN SETTINGS");
            scene.Refresh(Fleet, selected, now, settings.staleAfterSeconds, settings.reviewTdsPpm);
            Set("envelopeNote",scene.LayerNote);
            Set("layout", scene.Geographic ? "COORDINATE LAYOUT · NORMALIZED EXTENTS" : "SCHEMATIC LAYOUT · NOT GEOGRAPHIC");
            empty.style.display = Fleet.Wells.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            Set("emptyTitle", editorPreview ? "Your well network starts here" : IsDemo ? "Your demo workspace is ready" : "No sensor data available");
            Set("emptyMessage", editorPreview ? "Press Play to connect. The same dashboard is used in the web build." : IsDemo ? "Set the demo well count above to explore a network." : !settings.applyToDigitalTwin ? "Live updates are paused in Settings.\nUse Test demo to explore the local dataset." : connected ? "Broker connected; no usable sensor readings.\nCheck that the ESP32/Wokwi is running, publishing to this same broker and a subscribed topic.\nOpen Connection details for delivery counts. Test demo remains available." : "Use Test demo above to explore ten wells from local JSON.\nHiveMQ connectivity is shown separately in the header.");
            RefreshList(); RefreshDetails();
            var insight=guardian.Find(selected,IsDemo);
            Set("guardianSummary", insight==null ? "History, warnings and forecasts" : !insight.Ready(GuardianFeed.UtcNow) ? (insight.risk=="REVIEW"?"REVIEW · ":"")+insight.quality.Replace('_',' ') :
                insight.risk+" · "+insight.drawdown_cm_day.ToString("+0.##;-0.##;0",CultureInfo.InvariantCulture)+" cm/day");
            if (knownCount != Fleet.Wells.Count) { RebuildMarkers(); knownCount = Fleet.Wells.Count; }
            PlaceMarkers();
        }

        private void RefreshList()
        {
            filtered.Clear();
            foreach (var well in Fleet.Wells)
            {
                bool fresh = well.IsFresh(Time.unscaledTime, settings.staleAfterSeconds);
                if (!string.IsNullOrWhiteSpace(search.value) && well.Id.IndexOf(search.value, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (filter.index == 1 && !fresh || filter.index == 2 && (!fresh || !NeedsReview(well)) || filter.index == 3 && fresh) continue;
                filtered.Add(well);
            }
            list.RefreshItems();
            Set("listCount", filtered.Count + " / " + Fleet.Wells.Count);
            Set("listEmpty", filtered.Count == 0 ? "No wells match this view." : "");
        }
        private VisualElement MakeRow()
        {
            var button = new Button(); button.AddToClassList("well-row");
            button.Add(new Label { name = "id", enableRichText = false });
            button.Add(new Label { name = "values" });
            button.Add(new Label { name = "status" });
            button.clicked += () => SelectWell(button.userData as string);
            return button;
        }
        private void BindRow(VisualElement row, int index)
        {
            if (index >= filtered.Count) return;
            var well = filtered[index]; row.userData = well.Id;
            row.Q<Label>("id").text = well.Id;
            row.Q<Label>("values").text = F(well.Fill * 100, "0") + "% column   ·   " + F(well.Tds, "0") + " ppm";
            row.Q<Label>("status").text = Status(well);
            row.EnableInClassList("selected", selected == well.Id);
            row.EnableInClassList("stale", !well.IsFresh(Time.unscaledTime, settings.staleAfterSeconds));
            row.tooltip = well.Id + " · " + Status(well);
        }
        private bool NeedsReview(WellFleet.Well well)
        {
            var insight=guardian.Find(well.Id,IsDemo);
            return well.NeedsReview(settings.reviewTdsPpm,settings.lowWaterPercent/100)||
                (insight!=null&&insight.risk=="REVIEW"&&(insight.Example||insight.valid_until>=GuardianFeed.UtcNow));
        }
        private string Status(WellFleet.Well well)
        {
            if (well.IsCached) return "●  LAST KNOWN · WAITING FOR LIVE";
            if (!well.ReportedOnline) return "●  OFFLINE";
            if (!well.AgeKnown) return "●  RETAINED / AGE UNKNOWN";
            if (well.HasOldSourceTime(Time.unscaledTime, settings.staleAfterSeconds)) return "●  RECEIVING · SOURCE TIME OLD";
            if (!well.IsFresh(Time.unscaledTime, settings.staleAfterSeconds)) return "●  STALE · " + Age(well);
            if (NeedsReview(well)) return "●  REVIEW";
            return well.Demo ? "●  DEMO / REPORTING" : "●  REPORTING";
        }
        private string Age(WellFleet.Well well)
        {
            float age = well.Age(Time.unscaledTime);
            return age < 60 ? F(age, "0") + "s ago" : age < 3600 ? F(age / 60, "0") + "m ago" : F(age / 3600, "0") + "h ago";
        }
        private void RefreshDetails()
        {
            var well = Fleet.Find(selected);
            detailBody.style.display = well == null ? DisplayStyle.None : DisplayStyle.Flex;
            Set("selectedId", well?.Id ?? "Select a well");
            string origin=well==null?"":guardian.Find(well.Id,IsDemo)?.source;
            if(string.IsNullOrEmpty(origin)&&well!=null)origin=well.Demo||well.Id.ToUpperInvariant().Contains("SIM")||well.Id.ToUpperInvariant().Contains("WOKWI")?"simulated":"origin unverified";
            Set("selectedStatus", well == null ? "No telemetry received" : origin.ToUpperInvariant()+" · "+Status(well));
            if (well == null) return;
            Set("selectedFill", F(well.Fill * 100, "0.0") + "%");
            levelFill.style.width = Length.Percent(well.Fill * 100);
            Set("calibration", well.UsesLegacyCalibration ? F(settings.legacyWellDepthCm, "0.#") + " cm legacy calibration" : F(well.DepthCm + well.ColumnCm, "0.#") + " cm total reported depth");
            Set("depth", F(well.DepthCm, "0.0")); Set("column", F(well.ColumnCm, "0.0"));
            Set("tds", F(well.Tds, "0")); Set("ph", float.IsNaN(well.Ph) ? "Not supplied" : F(well.Ph, "0.00"));
            Set("tdsNote", well.Tds > settings.reviewTdsPpm ? "Above " + F(settings.reviewTdsPpm, "0") + " ppm review threshold" : "Below " + F(settings.reviewTdsPpm, "0") + " ppm review threshold");
            labels["tdsNote"].EnableInClassList("warning", well.Tds > settings.reviewTdsPpm);
            trend.Samples = well.History; trend.Timeout = settings.staleAfterSeconds; trend.MarkDirtyRepaint();
            Set("trendCount", well.History.Count + " / 120");
            bool enough = well.History.Count > 1;
            float min = enough ? well.History.Min(s => s.Depth) : 0;
            float max = enough ? well.History.Max(s => s.Depth) : 0;
            Set("trendRange", enough ? F(min, "0.0") + " – " + F(max, "0.0") + " cm below sensor" : "Waiting for a second observation");
            Set("trendNote", enough ? "Last " + F(well.History.Last().Time - well.History.First().Time, "0") + "s · depth increases downward" : "History builds from incoming readings.");
            Set("location", well.HasLocation ? well.Latitude.ToString("0.00000", CultureInfo.InvariantCulture) + ", " + well.Longitude.ToString("0.00000", CultureInfo.InvariantCulture) : "Location not supplied · schematic placement");
            string received = well.IsCached ? "Last known reading restored from this device · not a live sample"
                : (well.Retained ? "Retained snapshot received " : "Received ") + F(well.ReceiveAge(Time.unscaledTime), "0") + "s ago";
            string sourceTime = double.IsNaN(well.SourceTimestamp) ? "Source timestamp not supplied"
                : "Sensor UTC " + DateTimeOffset.FromUnixTimeSeconds((long)well.SourceTimestamp).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " · " + Age(well);
            Set("updated", received + "\n" + sourceTime);
        }
        private void RebuildMarkers()
        {
            markers.Clear(); pins.Clear();
            foreach (var well in Fleet.Wells)
            {
                string id = well.Id;
                var pin = new Button(() => SelectWell(id)) { text = id, tooltip = "Select " + id, enableRichText = false };
                pin.AddToClassList("well-pin"); markers.Add(pin); pins.Add(id, pin);
            }
        }
        private void PlaceMarkers()
        {
            foreach (var pair in pins)
            {
                Vector3 point = scene.Camera.WorldToViewportPoint(scene.HeadPosition(pair.Key));
                pair.Value.style.left = point.x * viewport.resolvedStyle.width;
                pair.Value.style.top = (1 - point.y) * viewport.resolvedStyle.height;
                pair.Value.style.display = point.z > 0 && point.x > 0 && point.x < 1 && point.y > 0 && point.y < 1 && (Fleet.Wells.Count<=6||pair.Key==selected||pair.Key==hovered) ? DisplayStyle.Flex : DisplayStyle.None;
                pair.Value.EnableInClassList("selected", pair.Key == selected);
                var well = Fleet.Find(pair.Key);
                pair.Value.EnableInClassList("stale", well != null && !well.IsFresh(Time.unscaledTime, settings.staleAfterSeconds));
            }
        }
        private void SetTwinMaximized(bool value)
        {
            twinMaximized=value;
            root.EnableInClassList("twin-maximized",value);
            maximizeButton.text=value?"Restore":"Maximize";
            root.Focus();
            root.schedule.Execute(()=>{if(scene!=null && viewport!=null){ResizeViewport();PlaceMarkers();}});
        }
        private void ResizeViewport()
        {
            if (viewport.resolvedStyle.width < 2 || viewport.resolvedStyle.height < 2) return;
            scene.Resize(Mathf.RoundToInt(viewport.resolvedStyle.width * panel.scale), Mathf.RoundToInt(viewport.resolvedStyle.height * panel.scale));
            viewportImage.image = scene.Texture; PlaceMarkers();
        }
        private void StepSelection(int delta)
        {
            if (Fleet.Wells.Count == 0) return;
            int index = Fleet.Wells.ToList().FindIndex(w => w.Id == selected);
            SelectWell(Fleet.Wells[(index + delta + Fleet.Wells.Count) % Fleet.Wells.Count].Id);
        }
        private void GenerateDemo()
        {
            if (demoDataset == null) return;
            demoDataset.Emit(session.DemoFleet, demoPlayhead, Time.unscaledTime,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, demoCount);
        }
        private void Metric(VisualElement parent, string key, string title, string value, string note, bool last = false)
        {
            var metric = Box(parent, "summary-metric"); metric.tooltip=note;
            if (last) metric.AddToClassList("summary-last");
            Text(metric, title, "eyebrow");
            KeyText(metric, key, value, "summary-value"); Text(metric, note, "metric-caption");
        }
        private void DetailMetric(string key, string title, string unit)
        {
            var metric = Box(detailBody, "detail-metric");
            var caption = Box(metric, "metric-caption-block"); Text(caption, title, "metric-label"); Text(caption, unit, "muted");
            KeyText(metric, key, "—", "detail-value");
        }
        private static VisualElement Box(VisualElement parent, string css)
        {
            var element = new VisualElement(); element.AddToClassList(css); parent.Add(element); return element;
        }
        private static Label Text(VisualElement parent, string text, string css)
        {
            var element = new Label(text) { enableRichText = false }; element.AddToClassList(css); parent.Add(element); return element;
        }
        private void KeyText(VisualElement parent, string key, string value, string css) { labels[key] = Text(parent, value, css); }
        private void Set(string key, string value) { labels[key].text = value; }
        private static string F(float value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
        private static void Action(VisualElement parent, string title, System.Action action, string tooltip)
        {
            var button = new Button(action) { text = title, tooltip = tooltip }; button.AddToClassList("tool"); parent.Add(button);
        }
        private static void ToggleButton(VisualElement parent, string title, Func<bool> toggle)
        {
            var button = new Button { text = title }; button.AddToClassList("tool"); button.AddToClassList("active");
            button.clicked += () => button.EnableInClassList("active", toggle()); parent.Add(button);
        }
        private void OnDestroy() { if (panel != null) { if(Application.isPlaying)Destroy(panel);else DestroyImmediate(panel); } if (instance == this) instance = null; }

        private sealed class Trend : VisualElement
        {
            public List<WellFleet.Sample> Samples;
            public float Timeout;
            public Trend() { generateVisualContent += Draw; pickingMode = PickingMode.Ignore; }
            private void Draw(MeshGenerationContext context)
            {
                var painter = context.painter2D;
                float width = contentRect.width, height = contentRect.height;
                if (width < 2 || height < 2) return;
                painter.strokeColor = new Color(.14f, .23f, .26f); painter.lineWidth = 1;
                for (int i = 0; i < 4; i++)
                {
                    float y = 5 + (height - 10) * i / 3;
                    painter.BeginPath(); painter.MoveTo(new Vector2(0, y)); painter.LineTo(new Vector2(width, y)); painter.Stroke();
                }
                if (Samples == null || Samples.Count < 2) return;
                float min = Samples.Min(s => s.Depth), max = Samples.Max(s => s.Depth);
                float padding = Mathf.Max(1, (max - min) * .15f); min -= padding; max += padding;
                float start = Samples[0].Time, duration = Mathf.Max(.01f, Samples[Samples.Count - 1].Time - start);
                painter.strokeColor = new Color(.33f, .88f, .75f); painter.lineWidth = 2;
                painter.BeginPath();
                for (int i = 0; i < Samples.Count; i++)
                {
                    var point = new Vector2((Samples[i].Time - start) / duration * width, 5 + (Samples[i].Depth - min) / (max - min) * (height - 10));
                    if (i == 0 || Samples[i].Time - Samples[i - 1].Time > Timeout) painter.MoveTo(point); else painter.LineTo(point);
                }
                painter.Stroke();
            }
        }
    }
}
