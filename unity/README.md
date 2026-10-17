# AquaTwin well network dashboard

AquaTwin discovers wells at runtime from HiveMQ telemetry. There is no configured live well count or fixed set of well objects. Every new identity creates its own state, 3D bore, selectable label and list entry. Subsequent readings update only that identity.

## Open the dashboard

1. Open this `unity` project with Unity **6000.3.2f1** and allow compilation/import to finish.
2. Enter **Play Mode**. The dashboard checks HiveMQ first, with a **10-second** connection/authentication/subscription timeout.
3. The header shows **HiveMQ / Connecting**, **Connected · Online**, or **Offline · Disconnected**. Online means MQTT authentication and both subscriptions succeeded, not just that a socket opened. A separate sensor indicator shows **No sensor data received**, **No fresh sensor data**, or the number of sensors reporting; broker connectivity never implies that readings exist.
4. Click the small **Test demo** button in the top-right corner whenever you want to test locally. It stays available while connecting, offline, connected without data, or showing only stale readings.
5. The local JSON sequence starts only after that click. Its amber **DEMO DATA · LOCAL JSON** bar is separate from the actual broker indicator.
6. Use **Pause / Resume**, **Restart demo**, the **Wells** input, and **Return to live** to explore the interface. **Retry HiveMQ** reloads saved credentials and retries without restarting Play Mode.

For live telemetry, open **gear > SETTINGS** in the dashboard, enter credentials, and choose **Save & apply**. Connection changes retry automatically. Missing credentials cause a single endpoint probe and an offline indication; the client waits for a manual retry rather than repeatedly attempting empty credentials. With configured credentials, failed connections retry every five seconds. The former serialized Simulation/LiveMqtt setting is retained for file compatibility; startup now always checks HiveMQ and never silently enables demo data.

The new dashboard is constructed at runtime. The existing authored presentation scene is hidden during Play Mode and preserved for editing; it is not necessary to rebuild that scene. The former 46-second recording remains in the repository but does not auto-run over this dashboard. The legacy editor scene builder never stops or rebuilds Play Mode when the runtime dashboard hides the old scene. Native rendering resources are initialized during the scene initialization method, not in MonoBehaviour field initializers.

## Dashboard settings and persistence

Use the **gear + SETTINGS** button beside the HiveMQ status indicator. A centered in-dashboard dialog matches the AquaTwin colours and typography, with a dimmed backdrop, scrollable content, Connection/Monitoring tabs and a responsive layout. Escape, Cancel, the close button or a backdrop click dismiss the draft without applying it. Keyboard Tab/Shift-Tab stay inside the open dialog.

**Connection** lets you edit the broker hostname, secure WebSocket port, path, username and masked password. Show password is optional. The two telemetry topic filters are displayed for reference and retain their existing contracts. **Monitoring** lets you pause live updates and edit legacy calibration, stale timeout, TDS review threshold and low-water threshold.

**Save & apply** validates values before writing them, shows an inline error if storage fails, and confirms successful saves with a toast. Monitoring changes apply immediately, including recalibration of existing legacy wells and updated TDS colours. Broker or credential changes trigger a reconnect. The active demo, its clock and its history remain intact. Opening AquaTwin > MQTT Connection Setup during Play Mode opens this same dashboard dialog; the old editor window remains available before Play Mode.

Preferences persist across application restarts:

- Unity Editor: the existing ignored `UserSettings/AquaTwinMqttCredentials.json` file, with old settings migrated automatically.
- Desktop player: `AquaTwinSettings.json` under `Application.persistentDataPath`.
- WebGL: the app-specific `AquaTwin.Settings.v1` preference through Unity PlayerPrefs. Unity uses browser IndexedDB for Web preferences; clearing site data resets these saved preferences. See [Unity PlayerPrefs storage](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/PlayerPrefs.html).

The password stays in memory for the current session unless **Remember password on this device** is selected. Remembered credentials are local and unencrypted; this is stated beside the option. Existing local files containing a password retain their prior remember behaviour during migration. The application does not embed saved credentials in Resources or a player build.

The settings UI and storage do not use UnityEditor APIs. Editor and WebGL conditional settings branches have been compiled, and 27 settings checks passed on Unity Mono and modern .NET, including actual file creation/replacement, serialization, migration, validation, session-only password omission and immediate calibration updates. Browser IndexedDB behaviour and the visual dialog still need a built-player check. **The existing live MQTT transport uses .NET ClientWebSocket; full live WebGL deployment additionally requires a browser WebSocket transport adapter and end-to-end testing.** This settings change does not claim that the full existing networking layer has been ported to WebGL; see [Unity Web networking](https://docs.unity3d.com/6000.0/Documentation/Manual/webgl-networking.html).

## Local JSON demonstration

The actual runtime resource is **`Assets/AquaTwin/Resources/AquaTwinDemo.json`**. It contains **10 wells and 50 keyframes**, with geographic coordinates, depths, water-column heights, TDS, optional pH, ONLINE/OFFLINE flags and transmission pauses. Values interpolate between keyframes and enter the same production telemetry registry used by live sensors every **two seconds**. Source timestamps are rebased to the current clock during replay; the fixed file never masquerades as current physical measurements.

The sequence loops every **90 seconds**. Nine wells arrive immediately; the tenth appears at 12 seconds to demonstrate runtime discovery. The demo count control selects 0–10 wells from this file. This dataset limit does not cap live well discovery.

| Well | Scenario |
|---|---|
| DEMO_001 | Stable reference and depth history |
| DEMO_002 | Gradual drawdown |
| DEMO_003 | Recharge and recovery |
| DEMO_004 | TDS rises across the review threshold |
| DEMO_005 | Optional pH absent; shows Not supplied |
| DEMO_006 | Low water warning, followed by recovery |
| DEMO_007 | TDS spike above 2000 ppm, followed by recovery |
| DEMO_008 | Stops transmitting from 18–60 seconds; becomes stale using the normal per-well timeout, then reconnects |
| DEMO_009 | Explicit OFFLINE status until 36 seconds, then ONLINE |
| DEMO_010 | First arrives at 12 seconds |

Search, filters, selection, metric summaries, 3D columns/colours, water envelope, history, zoom/orbit and layer controls all use this evolving dataset. Pausing stops emitted samples while their ages continue advancing, so it also lets you inspect stale-data behaviour. Restarting, changing the demo count or reaching the loop boundary resets the demo session and its history.

Broker monitoring continues during demo playback. Live packets go into a separate live registry, while the visible demo registry stays isolated. Reconnecting never silently replaces the demo. **Return to live** restores only real well data and its own history. Demo readings are never published to HiveMQ.

## Interface

- Overview: discovered identities, fresh reporting wells, fresh wells needing attention, and unweighted mean fill among reporting wells.
- Well network: a virtualized scrolling list with ID search and reporting, attention and stale/offline filters.
- Subsurface twin: procedural geological cross-section, independently sized water columns and TDS colours, and an optional shared water envelope. Click a well or its label to inspect it. Drag to orbit; scroll or use the buttons to zoom; Reset view fits the network.
- Well details: water fill, depth below the sensor, water column height, raw TDS, optional pH, location, data age and the last 120 depth observations. Previous/next buttons select wells independently of the list filter.
- Geology and Water envelope buttons hide or show the corresponding scene layers.

When all locations are present, distinct and non-degenerate, the view uses normalized latitude/longitude extents. Otherwise it uses a schematic grid. The layout indicator states which is active. Depth is normalized to each well's fill ratio; bore graphics are not a shared physical depth scale. The water envelope blends fresh well fill ratios for visual context; it is **not a surveyed or validated aquifer model**. Stale wells do not contribute to it or to current network metrics.

Operational review thresholds default to **TDS above 600 ppm** or **fill below 20%**, and are configurable. Optional pH is displayed as received. These are configurable monitoring indicators, not water-safety certification. The firmware currently synthesizes its pH value; displaying that channel does not establish that a physical pH probe exists.

## HiveMQ subscriptions

The existing secure WebSocket connection is retained:

- Host: `6a3cba0faab347c9a1a0c5afcfbd3ca3.s1.eu.hivemq.cloud`
- Port: `8884`; path: `/mqtt`; MQTT 3.1.1, subscriber QoS 0.
- Existing firmware family: **`digitaltwin/+/sensors`**.
- Development brief family: **`aquatwin/well/+/telemetry`**.

Your HiveMQ subscriber account needs permission for **both wildcard subscriptions**. Each topic segment must match the payload's device/well identity. Give each physical or simulated publisher its own unique identity and topic. Publishing every node as `well01` still represents one well.

The client handles MQTT packets split or combined across WebSocket frames, serializes writes, pings the broker, bounds incoming packet/queue sizes and reconnects after a disconnect. The main thread processes at most 256 queued events per frame; the pending telemetry queue holds at most 4096 events. Above that burst capacity, additional telemetry events are dropped until the queue drains. Each well retains at most 120 history samples; history is session-local and is not a database.

Broker connectivity and well freshness are separate. Each well becomes stale after the configured timeout (default **15 seconds**) even if other sensors are still reporting. Explicit OFFLINE status is respected. Timestamped retained messages use their source age. Legacy retained messages without a timestamp are labelled as unknown age and cannot count as fresh. Stale wells remain selectable and retain their last readings.

Credentials remain local in `UserSettings/AquaTwinMqttCredentials.json`, excluded from Git. No passwords are included in the implementation, and existing credentials are preserved.

## Supported telemetry contracts

Existing physical demonstration firmware continues to work on `digitaltwin/well01/sensors`:

```json
{
  "device": "well01",
  "distance_cm": 12.4,
  "tds_ppm": 342.5,
  "ph": 7.63
}
```

Legacy fill is calculated from **Legacy calibration (cm)** in MQTT Connection Setup, defaulting to the existing **20 cm** bottle calibration:

```text
water column = max(0, calibration - distance_cm)
fill = clamp(1 - distance_cm / calibration, 0, 1)
```

Use the canonical contract for wells with their own physical dimensions. The development-brief format is accepted on `aquatwin/well/WELL_ESP32_01/telemetry`:

```json
{
  "well_id": "WELL_ESP32_01",
  "location": { "lat": 24.4539, "lng": 54.3773 },
  "depth_to_water_cm": 142.5,
  "water_column_height_cm": 357.5,
  "tds_ppm": 320.0,
  "status": "ONLINE",
  "timestamp": 1792218000
}
```

**Replace the example timestamp with the current Unix time in seconds when publishing.** Historical timestamps correctly produce stale readings. Canonical fill is `water_column_height_cm / (depth_to_water_cm + water_column_height_cm)`. Optional numeric `ph` is supported in either contract; absent pH is displayed as **Not supplied**. Legacy messages may also include `location` and `timestamp`.

The strict parser rejects malformed JSON, missing required measurements, duplicate keys, string/null sensor values, negative depths/TDS, invalid coordinates, invalid pH, identity/topic mismatches, implausibly future timestamps and older source timestamps for an existing well. Raw TDS readings are preserved rather than clamped to 2000 ppm.

## Architecture and reference scope

```text
HiveMQ secure WebSocket -> HiveMqttWellDataSource
                        -> WellFleet identity registry
                        -> FleetDashboard + FleetScene
```

`WellFleet` has no Unity or networking dependency. `FleetDashboard.ApplyTelemetry` is an entry point for a future API adapter using the same well contracts. The repository does not currently contain the Python API, terrain-matrix protocol or persisted historical storage described in DEVELOPMENT.docx. This change retains the user-requested HiveMQ connection; it does not implement or deploy that separate backend or claim its interpolation output.

Main files:

- `Scripts/WellFleet.cs`: strict portable JSON parsing, identity routing, calibration, source age and history.
- `Scripts/HiveMqttWellDataSource.cs`: existing transport extended for two fleet subscriptions and reconnects.
- `Scripts/FleetDashboard.cs`: runtime UI Toolkit dashboard, connection-first flow, demo controls, selection, filtering and charts.
- `Scripts/DemoTelemetryDataset.cs`: validates the JSON dataset and replays keyframes through the production registry.
- `Scripts/FleetSession.cs`: isolates live/demo registries and broker status.
- `Resources/AquaTwinDemo.json`: editable ten-well example and scenario keyframes.
- `Scripts/FleetScene.cs`: procedural well objects, cutaway geology, water envelope and camera controls.
- `Resources/FleetDashboard.uss`, `FleetTheme.tss`, `FleetSurface.shader`: interface styling and bundled scene shader.
- `Scripts/DashboardSettingsDialog.cs`: runtime settings modal, keyboard focus and drawn gear icon.
- `Scripts/AquaTwinSettings.cs`: portable settings validation, encoding and file persistence.
- `Scripts/AquaTwinMqttLocalSettings.cs`: session settings and platform storage.
- `Editor/AquaTwinMqttSetupWindow.cs`: compatibility setup window and runtime dialog shortcut.
- `Editor/FleetValidation.cs`: offline data-contract regression checks, available through **AquaTwin > Validate Fleet Telemetry**.

## Validation

The production model and parser passed **126 checks**, including 100 additional discovered identities, independent stale detection, legacy/canonical calibration, retained data age, malformed input and bounded history. A further **30 demo/session checks** passed, covering the actual JSON playback, late discovery, silent/stale/offline/recovery scenarios, TDS spikes, zero/one well counts and isolation of live/demo data. Every JSON keyframe is additionally validated against the production telemetry contract when the dataset loads. Use **AquaTwin > Validate Demo Playback** to rerun the demo checks in the editor. The Unity source was compiled against the installed Unity 6000.3.2f1 assemblies. All 156 model/demo checks and 27 settings checks also passed using the bundled Unity Mono runtime, including regression checks for JSON longitude and fractional Unix timestamps. Demo numeric values are formatted independently of JSON braces to avoid Mono composite-format parsing differences.

An isolated full Unity run was attempted, but this execution environment could not connect to Unity's local package-manager/licensing IPC services. **Play Mode layout, shader appearance, mouse interaction, player builds and a live HiveMQ session remain to be verified in the user's Unity editor.** No screenshots or live-connectivity results are claimed.

For the first editor review, press Play, wait for the connection result and click Test demo. Try 0, 1 and 10 wells; select bores, filter the list and resize the Game view. Then configure HiveMQ credentials, retry, and return to live. Publish two different well IDs and stop one publisher long enough to confirm only that well becomes stale. Confirm both wildcard subscriptions are allowed by the broker account.
