# AquaTwin HiveMQ and Dynamic Backend Test

## Frozen contract

- HiveMQ TLS port: `8883`
- Publish topic: `aquatwin/well/{well_id}/telemetry`
- Backend subscription: `aquatwin/well/+/telemetry`
- Local API/dashboard: `http://127.0.0.1:8000`

Credentials stay in the Git-ignored `.env` file. Never put real passwords in
source, documentation, screenshots, commits, or chat.

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

The payload `well_id` must match the topic's `{well_id}` segment.

## Fully dynamic test

Terminal 1 starts secure MQTT ingestion, SQLite history, IDW calculation, API,
WebSocket, and the browser dashboard:

```bash
source .venv/bin/activate
python -m aquatwin_backend
```

Open `http://127.0.0.1:8000`.

Terminal 2 continuously publishes five clearly labelled mock wells every three
seconds:

```bash
source .venv/bin/activate
python mock_publisher.py --count 0 --wells 5 --interval 3
```

The dashboard must change after every cycle. Stop both processes with `Ctrl+C`.

## Verification endpoints

```bash
curl http://127.0.0.1:8000/health
curl http://127.0.0.1:8000/api/wells/live
curl http://127.0.0.1:8000/api/terrain-matrix
```

A healthy live test shows:

- MQTT `connected: true`
- increasing `valid_messages` and `update_sequence`
- five wells
- `ready: true`
- `48` rows, `48` columns, and `2304` values in each grid
- zero invalid messages

## Automated tests

```bash
python -m pytest -q
```

Tests do not require the broker. The real HiveMQ round-trip is a separate
integration check.
