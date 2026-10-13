# AquaTwin Multi-Well IDW Mapping

This module is Hadi/Salaar's numerical hand-off to Unity. It does not generate
ESP32 readings and does not render the final Unity scene.

## Repository audit and schema

No earlier Python IDW, spatial aggregation, heightmap generator, or
`DEVELOPMENT.md` exists in the current Git history. The Unity project contains
visual rendering code, but not the required multi-well interpolation math.

The active MQTT data contract is:

```text
topic: aquatwin/well/{well_id}/telemetry
fields: well_id, location.lat, location.lng, depth_to_water_cm,
        water_column_height_cm, tds_ppm, status, timestamp
```

Older repository documentation used `digitaltwin/well01/sensors` and a direct
single-well Unity MQTT connection. That is inconsistent with the current
multi-well architecture document. Spatial code therefore uses the active
multi-well schema and keeps the transport/rendering layers separate.

## Scientific interpretation

The ESP32 schema supplies depth below each local wellhead in centimetres. It
does **not** supply surveyed wellhead elevation relative to a shared datum.
Therefore the generated surface is explicitly named
`depth_to_water_below_local_wellhead`; it must not be presented as groundwater
elevation. A true groundwater elevation grid requires:

```text
groundwater_elevation = surveyed_wellhead_elevation - depth_to_water
```

## Processing

1. Validate required readings, numeric units, finite values, and coordinate
   ranges.
2. Keep the newest `timestamp` for each `well_id`.
3. Merge wells at identical coordinates using the arithmetic mean so a duplicate
   point is not accidentally double-weighted.
4. Convert latitude/longitude into local metre distances with a small-area
   equirectangular projection.
5. Apply IDW independently to depth and TDS with the default power `p = 2`:

```text
z(x,y) = sum(z_i / d_i^p) / sum(1 / d_i^p)
```

At zero distance, the exact measured value is returned rather than dividing by
zero.

## Generate Unity-ready JSON

The included input contains five clearly labelled synthetic wells and is not
field data:

```bash
source .venv/bin/activate
python generate_spatial_grid.py \
  --input examples/simulated_wells.json \
  --output output/spatial_grid.json \
  --preview output/spatial_grid_preview.svg \
  --rows 8 --columns 8 --power 2
```

The JSON contains flat row-major arrays, which are simpler for Unity to consume:

```text
grid.rows
grid.columns
grid.latitude_axis
grid.longitude_axis
grid.depth_to_water_cm
grid.tds_ppm
```

For grid coordinate `(row, column)`, the flat-array index is:

```text
index = row * columns + column
```

The professional preview contains a 3D depth surface plus a 2D TDS map with
labelled depth contours and well markers. It uses `-depth` only to inspect the
shape visually and explicitly states that it is not surveyed elevation.

## Verification

```bash
python -m pytest -q
```

Tests cover newest-reading aggregation, missing fields, invalid coordinates,
negative/non-finite values, conflicting packets, duplicate locations, separate
depth/TDS output, invalid grid parameters, the zero-distance case, and IDW
results checked against hand-calculated equations.

## Fully dynamic mode

The static generator is useful for reproducible testing. For live operation,
start the event-driven backend:

```bash
source .venv/bin/activate
python -m aquatwin_backend
```

It performs this pipeline for every accepted packet:

```text
HiveMQ aquatwin/well/+/telemetry
  -> strict schema validation
  -> SQLite history and latest-per-well aggregation
  -> 48x48 depth and TDS IDW calculation
  -> REST and WebSocket update
  -> browser dashboard / Unity client
```

Open `http://127.0.0.1:8000` to see the live 3D surface and TDS map. The
WebSocket is event-driven: each stored MQTT packet increments
`update_sequence` and immediately pushes a newly calculated grid. A 15-second
heartbeat also updates the live/stale indication.

Useful endpoints:

| Endpoint | Purpose |
|---|---|
| `/health` | MQTT connection, message counters, well count |
| `/api/wells/live` | Latest reading and freshness for each well |
| `/api/wells/{well_id}/history` | Historical samples for UI trends |
| `/api/terrain-matrix` | Current flattened 48x48 depth and TDS grids |
| `/ws/terrain-matrix` | Event-driven grid stream for Unity |

Continuous mock test:

```bash
python mock_publisher.py --count 0 --wells 5 --interval 3
```

Use `Ctrl+C` to stop the publisher and backend. Production ESP32/Wokwi nodes
replace the mock publisher without changing the backend or API contract.
