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
├── unity/                   # Unity digital twin project
│   ├── Assets/              # Scripts, materials, prefabs, scenes
│   ├── Packages/
│   ├── ProjectSettings/
│   └── README.md
├── data/                    # Recorded sensor data
│   ├── demo_40_second_reading.jsonl
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
Unity Digital Twin (subscribes via WSS, port 8884)
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
| HC-SR04 | TRIG | GPIO 4 | Output: controller → sensor |
| HC-SR04 | ECHO | GPIO 2 | Input: sensor → controller |
| HC-SR04 | VCC | 3V3 | |
| HC-SR04 | GND | GND | |
| TDS Probe | SIG | GPIO 1 | Analog in, ADC1_CH0 |
| TDS Probe | VCC | 3V3 | |
| TDS Probe | GND | GND | |

---

## Quick Start

### 1. Firmware

See [`firmware/README.md`](firmware/README.md).

1. Install the **ESP32 board package** in Arduino IDE (Boards Manager → search `esp32`).
2. Board settings: **ESP32S3 Dev Module**, USB CDC on boot enabled.
3. Open [`firmware/sketch.ino`](firmware/sketch.ino), set your Wi-Fi and MQTT credentials, upload.
4. Serial Monitor at **115200 baud** — sensor data and MQTT status.

### 2. Cloud Broker

See [`cloud/README.md`](cloud/README.md) for HiveMQ Cloud setup, broker config, and troubleshooting.

### 3. Unity Digital Twin

See [`unity/README.md`](unity/README.md).

1. Open the `unity/` folder as a project in Unity Hub (Unity 6000.3.2f1+).
2. Select **AquaTwin → MQTT Connection Setup** → set to `LiveMqtt` → enter credentials → Save.
3. Enter Play Mode — the 3D well responds to live sensor data.

---

## Data Contract

Every layer (firmware → broker → Unity) agrees on these keys:

| Key | Unit | Description |
|---|---|---|
| `device` | — | Device identifier (e.g. `well01`) |
| `distance_cm` | cm | Distance from sensor to water surface |
| `tds_ppm` | ppm | Total dissolved solids |
| `ph` | — | pH estimate |

Changing these keys requires updating firmware, Unity, and any backend in between.

---

## Team

Built by the AquaTwin team as part of CATCH_VR Summer School 2026 (GIK Institute, Erasmus+ CBHE project).

Contributors and their roles are preserved in the [commit history](../../commits/main).
