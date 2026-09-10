# Cloud Setup — HiveMQ MQTT

AquaTwin uses **HiveMQ Cloud** as the MQTT broker to relay sensor data from the ESP32-S3 to the Unity digital twin.

## Architecture

```text
ESP32-S3 (publisher)          HiveMQ Cloud              Unity (subscriber)
  firmware/sketch.ino    →    MQTT Broker (TLS)    →    HiveMqttWellDataSource.cs
       port 8883                    port 8884
```

The ESP32 publishes sensor JSON to a topic. Unity subscribes to the same topic over a secure WebSocket. The broker handles authentication and TLS for both sides.

## HiveMQ Cloud Broker

| Setting | Value |
|---|---|
| Host | `6a3cba0faab347c9a1a0c5afcfbd3ca3.s1.eu.hivemq.cloud` |
| MQTT port (TLS) | `8883` (used by ESP32) |
| WebSocket port (TLS) | `8884` (used by Unity) |
| WebSocket path | `/mqtt` |
| Full WSS endpoint | `wss://6a3cba0faab347c9a1a0c5afcfbd3ca3.s1.eu.hivemq.cloud:8884/mqtt` |
| Topic | `digitaltwin/well01/sensors` |

## Creating a HiveMQ Cloud Account

1. Go to [cloud.hivemq.com](https://cloud.hivemq.com/) and sign up for a free cluster.
2. Once the cluster is active, go to **Manage Cluster → Access Management**.
3. Create an MQTT user (e.g. `well_dt_esp32`) with a strong password.
4. Note the cluster ID shown in your dashboard — it becomes part of the host URL:
   ```
   <cluster-id>.s1.eu.hivemq.cloud
   ```
5. In the same Access Management panel, create a client access entry or use the default **Allow all** policy for development.

## ESP32 Firmware Configuration

In [`firmware/sketch.ino`](../firmware/sketch.ino), set:

```cpp
const char* WIFI_SSID       = "YOUR_WIFI_SSID";
const char* WIFI_PASSWORD   = "YOUR_WIFI_PASSWORD";
const char* MQTT_HOST       = "<cluster-id>.s1.eu.hivemq.cloud";
const int   MQTT_PORT       = 8883;
const char* MQTT_USERNAME   = "YOUR_MQTT_USERNAME";
const char* MQTT_PASSWORD   = "YOUR_MQTT_PASSWORD";
```

The firmware uses `WiFiClientSecure` with `setInsecure()` (no CA validation) for development. For production, replace with proper CA certificate validation.

### MQTT Payload

Published every 2 seconds to `digitaltwin/well01/sensors`:

```json
{
  "device": "well01",
  "distance_cm": 12.40,
  "tds_ppm": 342.50,
  "ph": 7.63
}
```

## Unity Subscriber Configuration

In the Unity Editor:

1. Select **AquaTwin → MQTT Connection Setup**.
2. Set **Data Mode** to `LiveMqtt`.
3. Enter the HiveMQ username and password.
4. Click **Save Local Settings**.
5. Enter Play Mode.

Settings are saved to `UserSettings/AquaTwinMqttCredentials.json` (excluded from Git via `.gitignore`).

### Verification

With the ESP32 publishing and Unity in Play Mode, the Console should show:

```text
[MQTT] Connecting...
[MQTT] Connected
[MQTT] Subscribed: digitaltwin/well01/sensors
[MQTT] Message received
distance_cm = ...
tds_ppm = ...
ph = ...
```

The connection indicator displays **LIVE** when data is flowing, **OFFLINE** if the connection drops or no valid message is received for 10 seconds.

## Simulation Mode (No ESP32 Required)

To demo without hardware:

1. Stop Play Mode.
2. Open **AquaTwin → MQTT Connection Setup**.
3. Select `Simulation`.
4. Save and restart Play Mode.

A prerecorded 46-second dataset feeds the same data pipeline.

## Data Contract

Every layer of the project (firmware → broker → Unity) agrees on these keys:

| Key | Unit | Description |
|---|---|---|
| `device` | — | Device identifier (e.g. `well01`) |
| `distance_cm` | cm | Distance from sensor to water surface |
| `tds_ppm` | ppm | Total dissolved solids |
| `ph` | — | pH estimate |

Changing these keys requires updating the firmware, the Unity subscriber, and any backend in between.
