# Firmware

Arduino sketches for the ESP32-S3 telemetry node. All files target the **ESP32-S3 DevKitC-1** board.

## Files

### `sketch.ino` — Production Firmware (MQTT)

The full production code. Connects to Wi-Fi and publishes sensor readings to HiveMQ Cloud over MQTT TLS (port 8883).

**Dependencies:** `WiFi.h`, `WiFiClientSecure.h`, `PubSubClient.h`

**Before uploading**, copy `secrets.example.h` to the Git-ignored `secrets.h` file and set:

| Constant | What to fill in |
|---|---|
| `AQUATWIN_WIFI_SSID` | Your Wi-Fi network name |
| `AQUATWIN_WIFI_PASSWORD` | Your Wi-Fi password |
| `AQUATWIN_MQTT_USERNAME` | HiveMQ Cloud MQTT username |
| `AQUATWIN_MQTT_PASSWORD` | HiveMQ Cloud MQTT password |

The production sketch synchronizes its clock with NTP and verifies HiveMQ's TLS
certificate against the included ISRG Root X1 CA. It deliberately refuses to
fall back to an insecure connection when time synchronization fails.

**MQTT topic:** `digitaltwin/well01/sensors`

**Payload format:**
```json
{
  "device": "well01",
  "distance_cm": 184.32,
  "tds_ppm": 410.50,
  "ph": 7.15
}
```

Publishes every 2 seconds.

---

### `sketch_serial_only.ino` — Serial-Only Version

Earlier version that outputs sensor data to the Serial Monitor only (no Wi-Fi/MQTT). Useful for bench testing without a network connection.

**Payload format (Serial @ 115200 baud):**
```json
{"depth_cm":184.32,"depletion_rate_cm_s":0.0120,"tds_ppm":410.5,"ph":7.15}
```

---

### `hc-sr04_test_code.ino` — Ultrasonic Sensor Test

Standalone test for the HC-SR04. Prints distance in cm and inches to Serial. Use this to verify wiring before touching the full sketch.

---

### `tds_test_code.ino` — TDS Sensor Test

Standalone test for the TDS probe on GPIO 1. Prints raw ADC value and converted voltage.

---

### `libraries.txt`

Wokwi library dependencies (currently: `ArduinoJson`). Used by the Wokwi simulator.

## Flashing

1. Install the **ESP32 board package** in Arduino IDE (Boards Manager → search `esp32`, install the Espressif package).
2. Board settings: **ESP32S3 Dev Module**, USB CDC on boot enabled if using the native USB port.
3. Open the desired `.ino` file, select the correct COM port, and upload.
4. Open Serial Monitor at **115200 baud**.
