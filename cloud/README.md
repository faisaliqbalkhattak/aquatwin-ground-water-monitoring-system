# AquaTwin Cloud Architecture and Setup

AquaTwin uses HiveMQ Cloud as the secure telemetry broker. Unity renders the
live visualization directly from the broker, while the optional Guardian
worker stores history and publishes decision-support insights.

```text
ESP32 / Wokwi nodes
  -> MQTT TLS 8883
  -> HiveMQ Cloud: aquatwin/well/{well_id}/telemetry
  -> Unity dashboard over secure WebSocket 8884
  -> Guardian worker over MQTT/TLS 8883
```

## Frozen contract

- HiveMQ device port (MQTT over TLS): `8883`
- HiveMQ dashboard port (MQTT over secure WebSocket): `8884`, path `/mqtt`
- Publish topic: `aquatwin/well/{well_id}/telemetry`
- Subscription topic filter: `aquatwin/well/+/telemetry`

The payload `well_id` must match the topic's `{well_id}` segment. Firmware,
Unity, and Guardian use this contract:

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
2. Create a second credential for subscribers (Unity dashboard and Guardian)
   with permission to subscribe to `aquatwin/well/+/telemetry`.
3. Give every physical or simulated node its own unique well identity. Each
   topic segment must match the payload's `well_id`; publishing every node as
   the same identity still represents one well.

Keep broker credentials in local configuration and sketch edits. Never put real
passwords in source, documentation, screenshots, commits, or chat.

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
- `guardian/`: subscribes to telemetry, stores validated history in SQLite,
  calculates insights, and publishes analysis and scenario responses. See
  `guardian/README.md`.

## Automated tests

```bash
cd guardian
python -m unittest discover -s tests -v
```

Tests do not require the broker. The real HiveMQ round-trip (firmware to Unity
and Guardian) is a separate integration check.
