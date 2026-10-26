# AquaTwin Cloud Architecture

AquaTwin uses HiveMQ Cloud only as the secure telemetry broker. The Python
backend is the sole downstream MQTT consumer; Unity consumes the Python REST or
WebSocket API and never stores HiveMQ credentials.

```text
ESP32/Wokwi nodes
  -> MQTT TLS 8883
  -> HiveMQ Cloud: aquatwin/well/{well_id}/telemetry
  -> Python backend subscription: aquatwin/well/+/telemetry
  -> SQLite + NumPy IDW
  -> REST / WebSocket on port 8000
  -> Unity and browser dashboard
```

## HiveMQ access

Create broker credentials under HiveMQ Cloud **Access Management**. Grant the
ESP32/Wokwi credential publish permission for its well topics, and grant the
backend credential subscribe permission for `aquatwin/well/+/telemetry`.

Copy `.env.example` to `.env` and fill in the private values locally:

```dotenv
MQTT_HOST=<cluster-id>.s1.eu.hivemq.cloud
MQTT_PORT=8883
MQTT_USERNAME=<private username>
MQTT_PASSWORD=<private password>
MQTT_TOPIC=aquatwin/well/+/telemetry
```

The backend uses the operating system CA store, requires a valid certificate,
verifies the hostname, and enforces TLS 1.2 or newer.

## Run

```bash
source .venv/bin/activate
python -m aquatwin_backend
```

Open `http://127.0.0.1:8000`. See [`../CLOUD_SETUP.md`](../CLOUD_SETUP.md) for
the continuous multi-well test and verification endpoints.
