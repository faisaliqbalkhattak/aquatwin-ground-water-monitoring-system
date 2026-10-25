# AquaTwin Cloud Architecture

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

## Data pipeline roles

- `firmware/sketch.ino`: publishes one contract on one topic family, whether
  readings come from hardware or from the built-in simulated wells.
- `unity/`: connects directly to HiveMQ (WSS 8884), discovers wells at runtime
  and renders the 3D digital twin; see `unity/README.md`.
- `cloud_monitor/`: validates every payload (`telemetry.py`), keeps
 thread-safe
 monitoring state (`state.py`) and renders a terminal dashboard (`dashboard.py`);
  see the commands in [`../CLOUD_SETUP.md`](../CLOUD_SETUP.md).

## HiveMQ access

Create broker credentials under HiveMQ Cloud **Access Management**. Grant the
firmware credential publish permission for its well topics, and grant the
subscriber credential subscribe permission for `aquatwin/well/+/telemetry`.

Copy `.env.example` to `.env` and fill in the private values locally:

```dotenv
MQTT_HOST=<cluster-id>.s1.eu.hivemq.cloud
MQTT_PORT=8883
MQTT_USERNAME=<private username>
MQTT_PASSWORD=<private password>
MQTT_TOPIC=aquatwin/well/+/telemetry
```

The monitor uses the operating system CA store, requires a valid certificate,
verifies the hostname, and enforces TLS 1.2 or newer.

Never commit real passwords. Keep them only in `.env` (monitor) or in local,
uncommitted sketch edits (firmware).
