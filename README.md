# AquaTwin 🌊

**A low-cost IoT + Digital Twin system for real-time groundwater depletion monitoring**

Built during the **Erasmus+ CATCH_VR Summer School 2026** at GIK Institute, under the theme *Immersive Technologies, IoT, and Digital Twin*. AquaTwin was our team's capstone project — it takes a well/aquifer, straps a couple of cheap sensors to it, and mirrors what's happening underground in a live 3D scene, so you don't have to wait for a manual survey to know a well is running dry.

This project aligns with UN SDG 6 (Clean Water and Sanitation)—focusing on Target 6.3 (improving water quality) and Target 6.4 (increasing water-use efficiency)—as well as UN SDG 11 (Sustainable Cities and Communities).

---

## Repository Structure

```
├── firmware/                # ESP32-S3 Arduino sketches
│   ├── sketch.ino           # Production firmware (MQTT + Wi-Fi)
│   ├── sketch_serial_only.ino  # Serial-only version (bench testing)
│   ├── hc-sr04_test_code.ino   # Ultrasonic sensor standalone test
│   ├── tds_test_code.ino       # TDS sensor standalone test
│   ├── libraries.txt           # Wokwi library dependencies
│   └── README.md
├── circuit/                 # Hardware simulation
│   ├── diagram.json         # Wokwi breadboard wiring
│   └── README.md
├── cloud/                   # MQTT broker setup
│   └── README.md
├── aquatwin_backend/        # Aggregation, validation, SQLite, and NumPy IDW
├── examples/                # Clearly labelled simulated multi-well input
├── generate_spatial_grid.py # Unity-ready depth/TDS JSON generator
├── SPATIAL_MAPPING.md       # IDW method, units, commands, and hand-off format
├── unity/                   # Unity digital twin project
│   ├── Assets/              # Scripts, materials, prefabs, scenes
│   ├── Packages/
│   ├── ProjectSettings/
│   └── README.md
└── README.md                # ← You are here
```

---

## What it actually does

Most groundwater monitoring in our context still happens through periodic manual surveys — someone walks out, drops a tape or a rope down a well, writes a number in a register. By the time that number reaches a decision-maker, the situation on the ground has already moved on.

AquaTwin replaces that with a small ESP32-based node sitting at the well head that:

1. Measures the distance to the water surface with an ultrasonic sensor (→ water depth)
2. Tracks how fast that depth is changing over time (→ depletion rate)
3. Reads a TDS probe to get a sense of dissolved solids in the water (→ salinity / quality proxy)
4. Packages all of it into a JSON payload and publishes it over MQTT to HiveMQ Cloud

That payload then drives a 3D digital twin of the well built in Unity — the water plane in the scene actually drops as the physical water level drops, and the tint of the water shifts as the readings change.

---

## Data Flow

```text
ESP32-S3 sensors
        ↓
HiveMQ Cloud (MQTT TLS, port 8883)
        ↓
Python backend (validation → SQLite → NumPy IDW)
        ↓
REST / WebSocket API (port 8000)
        ↓
Unity Digital Twin / live browser dashboard
        ↓
3D visualization: water level, TDS colour, pH, dashboard
```

---

## Hardware

Everything was picked to keep the prototype well under **4,000 PKR**, sourced locally.

| Component | Qty |
|---|---|
| ESP32-S3 DevKitC-1 (N16R8, dual Type-C) | 1 |
| HC-SR04 Ultrasonic Distance Sensor | 1 |
| TDS Meter V1.0 Module (waterproof probe) | 1 |
| Full-size 830-tie breadboard | 1 |
| Jumper wires (M-M + M-F, 20cm) | approx. 40 |

## Wiring & Pinout

| Module | Pin | ESP32-S3 Pin | Notes |
|---|---|---|---|
| HC-SR04 | TRIG | GPIO 5 | Output: controller → sensor |
| HC-SR04 | ECHO | GPIO 6 | Input: sensor → controller |
| HC-SR04 | VCC | 3V3 | |
| HC-SR04 | GND | GND | |
| TDS Probe | SIG | GPIO 3 | Analog in, ADC1_CH0 |
| TDS Probe | VCC | 3V3 | |
| TDS Probe | GND | GND | |

---

## Quick Start

### 1. Firmware

See [`firmware/README.md`](firmware/README.md).

1. Install the **ESP32 board package** in Arduino IDE (Boards Manager → search `esp32`).
2. Board settings: **ESP32S3 Dev Module**, USB CDC on boot enabled.
3. Copy `firmware/secrets.example.h` to `firmware/secrets.h`, fill in Wi-Fi/MQTT credentials, then upload `firmware/sketch.ino`.
4. Serial Monitor at **115200 baud** — sensor data and MQTT status.

### 2. Cloud Broker

See [`cloud/README.md`](cloud/README.md) for HiveMQ Cloud setup, broker config, and troubleshooting.
For the independent Python diagnostic monitor, see [`CLOUD_SETUP.md`](CLOUD_SETUP.md).

### 3. Unity Digital Twin

See [`unity/README.md`](unity/README.md).

1. Start the Python backend described below.
2. Open the `unity/` folder in Unity Hub (Unity 6000.3.2f1+).
3. The Unity developer consumes `/api/wells/live` and `/ws/terrain-matrix`;
   Unity must not connect directly to HiveMQ.

### Multi-Well IDW Grid

See [`SPATIAL_MAPPING.md`](SPATIAL_MAPPING.md) for the tested multi-well
aggregation and NumPy IDW workflow. It produces separate flat row-major depth
and TDS arrays for Unity, plus an optional local SVG surface preview.

For live operation:

```bash
source .venv/bin/activate
python -m aquatwin_backend
```

Open `http://127.0.0.1:8000` for the event-driven live map. For a continuous
five-well test, run `python mock_publisher.py --count 0 --wells 5 --interval 3`
in another terminal.

---

## Data Contract

Firmware and the Python ingestion layer agree on these MQTT keys:

| Key | Unit | Description |
|---|---|---|
| `well_id` | — | Well identifier (`WELL_WOKWI_01`) |
| `location.lat`, `location.lng` | degrees | Well coordinates |
| `depth_to_water_cm` | cm | Sensor-to-water distance |
| `water_column_height_cm` | cm | Water column height |
| `tds_ppm` | ppm | Total dissolved solids |
| `status` | — | Device state, such as `ONLINE` |
| `timestamp` | Unix seconds | Device publication time |

Changing these keys requires updating firmware, Unity, and any backend in between.

---

## Team

Built by the AquaTwin team as part of CATCH_VR Summer School 2026 (GIK Institute, Erasmus+ CBHE project).

Contributors and their roles are preserved in the [commit history](../../commits/main) and [commit history](../../commits/unity_code).
