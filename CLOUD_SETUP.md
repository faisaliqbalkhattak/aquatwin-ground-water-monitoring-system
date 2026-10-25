# AquaTwin HiveMQ Cloud Setup

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
Credentials stay out of Git: passwords live in `.env` (monitor) and in local
sketch edits (firmware). Never put real passwords in source, documentation,
screenshots, commits, or chat.

## HiveMQ Cloud access management

1. In HiveMQ Cloud **Access Management**, create one credential for the
   ESP32/Wokwi publisher with permission to publish to
   `aquatwin/well/{well_id}/telemetry`.
2. Create a second credential for subscribers (Unity dashboard and
   `cloud_monitor`) with permission to subscribe to `aquatwin/well/+/telemetry`.
3. Give every physical or simulated node its own unique well identity. Each
   topic segment must match the payload's `well_id`; publishing every node as
   the same identity still represents one well.

## Firmware (ESP32 / Wokwi)

`firmware/sketch.ino` publishes simulated and live readings to
`aquatwin/well/{well_id}/telemetry`. Set the WiFi SSID/password and the HiveMQ
host, username and password in the configuration block at the top of the file.
The committed file contains placeholders only; fill in real values locally and
never commit them.

## Unity dashboard

The Unity project (`unity/`) connects directly to HiveMQ over secure WebSocket
port `8884` and renders the well dashboard itself. Open the gear > SETTINGS
dialog in Play Mode and enter the subscriber credentials saved from Access
Management. Unity never needs a separate backend or API.

## Cloud monitor

The monitor subscribes with the subscriber credential:

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
