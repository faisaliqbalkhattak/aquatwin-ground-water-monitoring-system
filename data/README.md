# Data

Recorded sensor data from demo runs.

## `demo_40_second_reading.jsonl`

~40 seconds of serial output captured during a live demo. Each line is a JSON object matching the data contract:

```json
{"depth_cm":15.68,"depletion_rate_cm_s":0.0000,"tds_ppm":274.5,"ph":7.50}
```

### Fields

| Key | Unit | Description |
|---|---|---|
| `depth_cm` | cm | Distance from sensor to water surface |
| `depletion_rate_cm_s` | cm/s | Rate of depth change (positive = water dropping) |
| `tds_ppm` | ppm | Total dissolved solids |
| `ph` | — | pH estimate (dynamic placeholder in prototype) |

### What the data shows

The recording captures a short demo where:
1. **Stable phase** — depth holds at ~15.68 cm, TDS ~275 ppm, pH ~7.5
2. **Depletion event** — depth drops from ~15.68 cm to ~9.0 cm, depletion rate peaks at ~0.65 cm/s, TDS rises to ~1370 ppm
3. **Post-event** — readings stabilize at the new level

This data was used to verify the digital twin's water-plane animation and shading logic in Unity.
