# AquaTwin 🌊

**A low-cost IoT + Digital Twin system for real-time groundwater depletion monitoring**

Built during the **Erasmus+ CATCH_VR Summer School 2026** at GIK Institute, under the theme *Immersive Technologies, IoT, and Digital Twin*. AquaTwin was our team's capstone project — it takes a well/aquifer, straps a couple of cheap sensors to it, and mirrors what's happening underground in a live 3D scene, so you don't have to wait for a manual survey to know a well is running dry.

This project aligns with UN SDG 6 (Clean Water and Sanitation)—focusing on Target 6.3 (improving water quality) and Target 6.4 (increasing water-use efficiency)—as well as UN SDG 11 (Sustainable Cities and Communities).

---

## Repository Structure

```
├── firmware/                # ESP32-S3 Arduino sketches
│   ├── sketch/sketch.ino    # Production firmware (MQTT + Wi-Fi)
│   ├── sketch_serial_only.ino  # Serial-only version (bench testing)
│   ├── hc-sr04_test_code.ino   # Ultrasonic sensor standalone test
│   ├── tds_test_code.ino       # TDS sensor standalone test
│   ├── libraries.txt           # Wokwi library dependencies
│   └── README.md
├── circuit/                 # Hardware simulation
│   ├── diagram.json         # Wokwi breadboard wiring
│   └── README.md
├── cloud/                   # MQTT broker setup and telemetry contract
│   └── README.md
├── cloud_monitor/           # Optional Python MQTT terminal monitor
├── guardian/                # Optional durable history and decision-support service
├── demos-and-presentations/ # Demonstration video and presentation
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

### Guardian decision support

The optional [Guardian service and deployment guide](guardian/README.md) adds durable SQLite history, early-warning trends, evidence-gated forecasts, hypothetical pumping scenarios and historical Abu Dhabi context. Unity implementation remains in `unity/`; the worker and public map page live separately in `guardian/`. The existing raw telemetry and offline demo remain usable without the worker.

```text
ESP32-S3 sensors
        ↓
HiveMQ Cloud (MQTT TLS, port 8883)
        ↓
Unity Digital Twin / cloud monitor
        ↓
3D visualization: water level, TDS colour, and dashboard state
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
3. Copy `firmware/secrets.example.h` to `firmware/secrets.h`, fill in Wi-Fi/MQTT credentials, then upload `firmware/sketch/sketch.ino`.
4. Serial Monitor at **115200 baud** — sensor data and MQTT status.

### 2. Cloud Broker

See [`cloud/README.md`](cloud/README.md) for HiveMQ Cloud setup, broker config, the
frozen telemetry contract, and the cloud monitor commands.

### 3. Unity Digital Twin

See [`unity/README.md`](unity/README.md).

1. Open the `unity/` folder in Unity Hub (Unity 6000.3.2f1+).
2. Use the local ten-well demo to demonstrate the dashboard without hardware or
   broker credentials, or configure the MQTT connection for live telemetry.
3. Unity subscribes to HiveMQ through its configured secure WebSocket connection;
   local credentials are stored outside Git and must never be committed.

### 4. Cloud monitor and Guardian service

The optional `cloud_monitor` package provides a terminal view of validated
telemetry. The optional [Guardian service](guardian/README.md) adds durable
history and decision-support features. Both can be run independently of the
Unity offline demo.

To run the cloud monitor:

```bash
python -m cloud_monitor --env-file .env
```

Create `.env` locally from `.env.example` and provide broker credentials only
when using live telemetry. The offline Unity demo does not require `.env`.

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

### Contributions

| Member | Responsibility |
|---|---|
| Faisal Iqbal | Team lead, project coordination, system integration, and final documentation |
| Muhammad Rahat Shafi | Unity digital-twin development and dashboard implementation |
| Muhammad Ahmed Raza | Embedded coding, sensor integration, and firmware work |

## License

This project is released under the [MIT License](LICENSE).
