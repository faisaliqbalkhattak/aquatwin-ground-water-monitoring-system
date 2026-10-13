# AquaTwin Unity and HiveMQ Setup

## Overview

The AquaTwin Unity project visualizes a physical demonstration well in Unity Editor Play Mode. It shows water level, distance to the water surface, TDS, pH, quality indicators, and the 3D water column.

The project supports two interchangeable data sources:

- **Live MQTT:** readings published by the ESP32-S3 through HiveMQ Cloud.
- **Simulation:** the existing 46-second prerecorded dataset, retained as a demonstration fallback.

Both sources feed the same `WellVisualizer` data pipeline, so the existing model and dashboard do not need separate live-data UI logic.

## Data Flow

```text
ESP32-S3 sensors
        ↓
HiveMQ Cloud
        ↓  secure WebSocket (MQTT 3.1.1)
HiveMqttWellDataSource.cs
        ↓
WellVisualizer.cs
        ↓
Existing 3D well and dashboard UI
```

The Unity MQTT client is subscriber-only; it does not publish commands or data.

## HiveMQ Configuration

| Setting | Value |
|---|---|
| Host | `6a3cba0faab347c9a1a0c5afcfbd3ca3.s1.eu.hivemq.cloud` |
| Secure WebSocket port | `8884` |
| WebSocket path | `/mqtt` |
| Endpoint | `wss://6a3cba0faab347c9a1a0c5afcfbd3ca3.s1.eu.hivemq.cloud:8884/mqtt` |
| Topic | `digitaltwin/well01/sensors` |
| Username | Enter your HiveMQ Access Management username locally |

The password is intentionally not stored in Git. Each user must obtain it privately from the project/cloud administrator.

## Connecting Unity

1. Open the repository's current `unity/` folder in Unity Hub.
2. Open it with **Unity 6000.3.2f1**.
3. Wait for Unity to import packages and finish compiling.
4. In Unity, select **AquaTwin → MQTT Connection Setup**.
5. Set **Data Mode** to `LiveMqtt`.
6. Enter the HiveMQ username and private password.
7. Keep **Update Digital Twin** enabled.
8. Select **Save Local Settings**.
9. Enter Play Mode.

The settings are saved locally in:

```text
UserSettings/AquaTwinMqttCredentials.json
```

`UserSettings` is excluded by `.gitignore`, so the password must never be committed. Stop and restart Play Mode after changing the data mode.

## Incoming Data Format

Unity expects the agreed well JSON on `digitaltwin/well01/sensors`:

```json
{
  "well_id": "WELL_WOKWI_01",
  "location": {"lat": 24.453900, "lng": 54.377300},
  "depth_to_water_cm": 143.14,
  "water_column_height_cm": 356.86,
  "tds_ppm": 320.3,
  "status": "ONLINE",
  "timestamp": 1791700194
}
```

`depth_to_water_cm` is the ultrasonic distance from the sensor to the water.
Unity derives total depth from `depth_to_water_cm + water_column_height_cm`.

## Water-Level Calibration

For live well telemetry, `WellVisualizer` owns the conversion:

```text
water level (%) = water_column_height_cm /
                  (depth_to_water_cm + water_column_height_cm) × 100
```

Examples:

The legacy 20 cm calibration remains available only for older payloads during migration.

The result is clamped to 0–100%. TDS controls the water colour. Since the new
contract has no pH field, the current visual pH value is left unchanged.

## Verification

With the ESP32 publishing and Unity in Play Mode, the Console should show:

```text
[MQTT] Connecting...
[MQTT] Connected
[MQTT] Subscribed:
digitaltwin/well01/sensors
[MQTT] Message received
depth_to_water_cm = ...
water_column_height_cm = ...
tds_ppm = ...
status = ONLINE
```

After a valid message, the existing connection indicator displays **LIVE**. It displays **OFFLINE** if the connection closes or no valid message is received for 10 seconds. Connection, authentication, malformed JSON, and missing-field errors are logged without stopping the Digital Twin. A dropped WSS connection is retried every 5 seconds until Play Mode stops.

## Using Simulation Mode

To demonstrate the project without the ESP32:

1. Stop Play Mode.
2. Open **AquaTwin → MQTT Connection Setup**.
3. Select `Simulation`.
4. Save the local settings.
5. Restart Play Mode.

The prerecorded 46-second provider then feeds the same `WellVisualizer` pipeline. In `LiveMqtt` mode, prerecorded playback is disabled to prevent it from overwriting physical sensor readings.

## Main Unity Scripts

- `HiveMqttWellDataSource.cs` — secure WebSocket connection, MQTT subscription, JSON validation, console output, timeout handling, and forwarding of valid readings.
- `AquaTwinMqttLocalSettings.cs` — broker constants, data-mode selection, and local credential loading.
- `AquaTwinMqttSetupWindow.cs` — Unity Editor window for selecting Live/Simulation mode and saving private credentials locally.
- `WellVisualizer.cs` — shared sensor entry point, 20 cm water calibration, water movement, water colour, TDS, and pH state.
- `RecordedTelemetryPlayback.cs` — retained simulation source; automatically disabled in Live MQTT mode.
- `RegionalDashboardController.cs` — refreshes the existing dashboard from `WellVisualizer` values.
