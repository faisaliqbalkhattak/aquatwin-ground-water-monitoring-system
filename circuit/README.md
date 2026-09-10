# Circuit

Wokwi simulation diagram for the AquaTwin hardware setup.

## `diagram.json`

Wokwi project file containing the breadboard wiring for:

- **ESP32-S3 DevKitC-1** (N16R8, dual Type-C)
- **HC-SR04** ultrasonic distance sensor
- **TDS Meter V1.0** module (simulated via slide potentiometer)

Open this file directly in the [Wokwi Simulator](https://wokwi.com/) to run the firmware in-browser.

## Pin Mapping

| Module | Pin | ESP32-S3 Pin | Direction | Notes |
|---|---|---|---|---|
| HC-SR04 | TRIG | GPIO 4 | Output | Controller → Sensor |
| HC-SR04 | ECHO | GPIO 2 | Input | Sensor → Controller |
| HC-SR04 | VCC | 3V3 | Power | |
| HC-SR04 | GND | GND | Ground | |
| TDS Probe | SIG | GPIO 1 | Analog in | ADC1_CH0 |
| TDS Probe | VCC | 3V3 | Power | |
| TDS Probe | GND | GND | Ground | |

## Wire Colors (convention used in the diagram)

- **Red** — 5V / 3.3V power
- **Black** — Ground
- **Green** — Data out (TRIG)
- **Yellow** — Data in (ECHO)
- **Blue** — Analog signal (TDS)
