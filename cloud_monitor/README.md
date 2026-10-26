# AquaTwin Cloud Monitor

The cloud monitor is an optional terminal subscriber for live AquaTwin
telemetry. It connects to HiveMQ Cloud over MQTT/TLS, validates incoming
messages, and displays the latest state of the monitored well.

## Requirements

- Python 3.11 or newer
- The packages in the repository `requirements.txt`
- A local `.env` file containing the HiveMQ connection settings

The offline Unity demo does not require the cloud monitor or broker credentials.

## Setup

From the repository root:

```bash
python -m pip install -r requirements.txt
copy .env.example .env
```

Edit `.env` and provide the MQTT username and password locally. Never commit
`.env` or any file containing real credentials.

## Run

```bash
python -m cloud_monitor --env-file .env
```

The monitor subscribes to the configured `MQTT_TOPIC`, validates telemetry,
tracks valid and invalid message counts, and reports the latest reading and its
age. The default topic is:

```text
aquatwin/well/+/telemetry
```

## Validation behavior

The monitor rejects malformed JSON, invalid numeric values, invalid well
identities, unsupported statuses, invalid locations, future timestamps, and
out-of-order readings. Invalid messages are counted and do not replace the
latest valid state.

## Source files

- `client.py` — MQTT connection and message loop
- `config.py` — environment-file configuration
- `telemetry.py` — telemetry parsing and validation
- `state.py` — latest valid state and counters
- `dashboard.py` — terminal rendering

