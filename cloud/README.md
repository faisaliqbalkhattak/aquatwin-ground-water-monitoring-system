# AquaTwin Cloud Architecture and Setup

AquaTwin uses HiveMQ Cloud only as the secure telemetry broker. There is no
separate backend: the Unity dashboard and the `cloud_monitor` terminal tool are
the downstream MQTT consumers, and Unity renders its own visualization directly
from the broker.

```text
ESP32 / Wokwi nodes
  -> MQTT TLS 8883
  -> HiveMQ Cloud: aquatwin/well/{well_id}/telemetry
  -> Unity dashboard over secure WebSocket 8884 (own UI)
  -> cloud_monitor terminal monitor (subscriber) via cloud_monitor/
```

## Frozen contract

- HiveMQ device port (MQTT over TLS): `8883`
- HiveMQ dashboard port (MQTT over secure WebSocket): `8884`, path `/mqtt`
- Publish topic: `aquatwin/well/{well_id}/telemetry`
- Subscription topic filter: `aquatwin/well/+/telemetry`

The payload `well_id` must match the topic's `{well_id}` segment. Firmware, the
Unity dashboard and the cloud monitor all use this one contract:

```json
{
  "well_id": "WELL_WOKWI_01",
  "location": {"lat": 24.4539, "lng": 54.3773},
  "depth_to_water_cm": 143.14,
  "water_column_height_cm": 356.86,
  "tds_ppm": 320.3,
  "status": "ONLINE",
  "timestamp": 1791700194
}
```

Replace `timestamp` with the current Unix time in seconds when publishing.

## HiveMQ Cloud access management

1. In HiveMQ Cloud **Access Management**, create one credential for the
   ESP32/Wokwi publisher with permission to publish to
   `aquatwin/well/{well_id}/telemetry`.
2. Create a second credential for subscribers (Unity dashboard and
   `cloud_monitor`) with permission to subscribe to `aquatwin/well/+/telemetry`.
3. Give every physical or simulated node its own unique well identity. Each
   topic segment must match the payload's `well_id`; publishing every node as
   the same identity still represents one well.

Copy `.env.example` to `.env` and fill in the private values locally:

```dotenv
MQTT_HOST=<cluster-id>.s1.eu.hivemq.cloud
MQTT_PORT=8883
MQTT_USERNAME=<private username>
MQTT_PASSWORD=<private password>
MQTT_TOPIC=aquatwin/well/+/telemetry
```

Credentials stay out of Git: passwords live in `.env` (monitor) and in local
sketch edits (firmware). Never put real passwords in source, documentation,
screenshots, commits, or chat.

## Data pipeline roles

- The ESP32/Wokwi firmware (`firmware/sketch/sketch.ino`) publishes one
  contract on one topic family, whether readings come from hardware or from
  the built-in simulated wells. Set the WiFi SSID/password and the HiveMQ
  host, username and password in the configuration block at the top of the
  file; fill in real values locally and never commit them.
- `unity/`: connects directly to HiveMQ (WSS 8884), discovers wells at runtime
  and renders the 3D digital twin. Open the gear > SETTINGS dialog in Play
  Mode and enter the subscriber credentials saved from Access Management.
  Unity never needs a separate backend or API; see `unity/README.md`.
- `cloud_monitor/`: validates every payload (`telemetry.py`), keeps
  thread-safe monitoring state (`state.py`) and renders a terminal dashboard
  (`dashboard.py`). The monitor uses the operating system CA store, requires a
  valid certificate, verifies the hostname, and enforces TLS 1.2 or newer.

## Run the cloud monitor

```bash
cp .env.example .env        # fill in MQTT_HOST, MQTT_USERNAME, MQTT_PASSWORD
python -m cloud_monitor --env-file .env
```

A terminal dashboard prints live readings and turns stale after the configured
`MQTT_STALE_TIMEOUT` seconds without a valid message.

## Automated tests

```bash
python -m pytest -q
```

Tests do not require the broker. The real HiveMQ round-trip (firmware to Unity
and the monitor) is a separate integration check.
