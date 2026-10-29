# AquaTwin Guardian: setup and deployment

The Unity dashboard and the Guardian worker are separate components. Unity stays in `../unity/`; this directory contains persistence, analysis, tests, deployment files and the historical map page.

## What was implemented

| Feature area | Implemented behaviour | Where to use it |
|---|---|---|
| Early warning | Minute medians, Theil-Sen depth/TDS rates, change from baseline, residual anomaly score, evidence window/count, review reasons, optional same-aquifer nearby-well correlation and response after a configured pumping pause | Select a well -> Insights -> Evidence |
| Time Machine | Bounded linear projections, heuristic uncertainty band, critical-depth and TDS-review crossing estimates, chronological holdout errors, historical/forecast chart, timeline and orange ghost column | Insights -> Time machine -> View twin |
| Intervention | Enter flow, runtime, reduction, start day and recharge assumptions; compare abstraction and avoided volume; optionally enter effective area and specific yield for a hypothetical head response and sensitivity band; persist and compare scenarios | Insights -> What-if -> Calculate & save -> View twin |
| Spatial layers | Water-column/head pattern, estimated-TDS pattern with coverage fading, hypothetical pumping footprint, and a separate historical EAD map with years and source legend | Main layer selector; Insights -> Abu Dhabi context |
| Supporting architecture | SQLite raw events/minute aggregates/insight runs/scenario runs; deduplication; source/freshness flags; versioned retained insight messages; non-retained scenario request/reply; CSV export and safe SQLite backup | Guardian worker / CLI |

## Unity startup and interface changes

- `SampleScene` now contains the network dashboard. Retired single-well visual objects were removed from that scene; the legacy scripts/assets remain available. Their automatic scene builder is disabled.
- Outside Play Mode, a disposable preview of the same new interface appears. It is labelled **Editor preview** and makes no automatic MQTT connection. Press Play for monitoring. Preview objects are not saved into the build.
- The scene is included in the shared build scene list. Existing additional scenes are preserved.
- Primary measurements and status use larger type. Repeated captions and branding were reduced. pH, calibration, timestamps, location and the existing depth graph remain under **History & sensor details**.
- **Connection details** retains subscription filters, errors, keep-alive replies and the retry action. MQTT failure does not remove demo mode.
- Wells are still discovered at runtime; no well-count limit of 3, 8 or 10 was added. Ten applies only to the bundled offline demonstration.

## Deployment architecture

```text
ESP32 / Wokwi -> HiveMQ raw telemetry -> Unity WebGL on Hostinger
                       |
                       +-> Guardian worker -> SQLite history
                                 |
                                 +-> HiveMQ insight topics -> Unity
Unity scenario request -> HiveMQ -> Guardian -> HiveMQ reply -> Unity
```

Your existing Hostinger PHP/HTML site can continue serving the Unity build. A normal shared web-hosting directory does not run the always-on Python worker. Run that worker on your computer for a demonstration, or an always-on VPS/server for continuous history. A Hostinger VPS is one possible location; the static website does not need to move. No incoming API port, browser CORS proxy or second public HTTPS endpoint is needed because integration uses the existing HiveMQ WSS connection.

Hostinger's current platform guidance: <https://www.hostinger.com/support/which-programming-languages-and-frameworks-are-supported-at-hostinger/>. Paho API: <https://eclipse.dev/paho/clients/python/docs/>.

## 1. Configure HiveMQ permissions

Keep using the same cluster for sensor, worker and dashboard. Use separate credentials/roles where practical:

| Client | Subscribe | Publish |
|---|---|---|
| Sensor | none required | `aquatwin/well/+/telemetry` |
| Guardian worker | `aquatwin/well/+/telemetry`, `aquatwin/guardian/requests/+` | `aquatwin/insights/well/+`, `aquatwin/guardian/replies/+`, `aquatwin/guardian/status` |
| Unity dashboard | Its existing raw telemetry filters, plus `aquatwin/insights/well/+`, `aquatwin/guardian/replies/+`, `aquatwin/guardian/status` | `aquatwin/guardian/requests/+` |

The Unity client adds its own reply subscription automatically. If an optional Guardian subscription is denied, raw well monitoring continues and Connection details reports the missing permission.

Native sensors/worker normally use MQTT/TLS port **8883**. The website uses WSS **8884** with `/mqtt`. The worker can alternatively use `"transport":"websockets", "broker_port":8884`. TLS certificate verification is enabled on the worker.

## 2. Run Guardian locally on Windows

Install Python 3.10 or newer. Open PowerShell in this `guardian` directory:

```powershell
py -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
Copy-Item -LiteralPath config.example.json -Destination config.local.json
```

Edit `config.local.json`:

1. Set your real broker hostname.
2. Replace the sample well ID under `wells` with actual IDs, or leave `wells` empty to discover wells automatically.
3. Mark confirmed physical data `"source":"measured"`; simulated IDs containing SIM/WOKWI/DEMO default to `simulated`. Other unidentified origins default to `unverified`. Do not mark Wokwi as physical measurement.
4. Set a real **critical operating depth** using pump/sensor geometry. Do not use a limit beyond the sensor's valid range. The example 360 cm threshold is reachable within an example 400 cm ultrasonic range; it is not a universal operating recommendation.
5. Only set `aquifer_id`, `reference_elevation_m` and `vertical_datum` when known. Null values keep the 3D water envelope explicitly schematic.
6. Set `tds_calibrated` only after actual calibration. No code update can perform physical TDS calibration.

Enter the backend credentials into the current shell without writing them into the project:

```powershell
$guardianCredential = Get-Credential -Message "HiveMQ Guardian worker credentials"
$env:AQUATWIN_MQTT_USERNAME = $guardianCredential.UserName
$env:AQUATWIN_MQTT_PASSWORD = $guardianCredential.GetNetworkCredential().Password
.\.venv\Scripts\python.exe -m guardian_core run --config config.local.json
```

Leave the worker and sensor running. The worker creates `data/guardian.sqlite3`. Closing Unity does not stop collection. Closing the worker does; it cannot reconstruct telemetry that was never collected. A retained broker snapshot is not a long history.

The logs identify connection/subscription failures and stored well IDs. They omit passwords and raw payloads. Press Ctrl+C to stop. Clear the shell secrets afterwards:

```powershell
Remove-Item Env:\AQUATWIN_MQTT_PASSWORD, Env:\AQUATWIN_MQTT_USERNAME -ErrorAction SilentlyContinue
```

In Unity or the hosted website, open Settings, use dashboard credentials, then select a well and open **Insights**. No analytics URL needs to be entered.

## 3. Run continuously on a VPS

### Docker Compose option

On a server with Docker Compose already installed, copy this `guardian` directory to a private application directory. Create `config.local.json` as above and `.env` from `.env.example`, with your backend credentials. Keep both outside any public web directory.

```bash
docker compose up -d --build
docker compose logs -f --tail=100 guardian
```

The `guardian_history` named volume holds SQLite across container restarts/rebuilds. Do not run `docker compose down -v` unless you intend to erase that history. The container exposes no public port. If Docker is unavailable, use the systemd option below.

### systemd option

Install Python, copy the worker to `/opt/aquatwin/guardian`, create a `.venv` and install `requirements.txt`. Create a dedicated `aquatwin` service user/group. Put the configuration at `/etc/aquatwin-guardian.json` and the two credential environment assignments at `/etc/aquatwin-guardian.env` (readable only by root/service administrator). Copy `deploy/aquatwin-guardian.service` to `/etc/systemd/system/`:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now aquatwin-guardian
sudo journalctl -u aquatwin-guardian -f
```

The unit stores history in `/var/lib/aquatwin/guardian.sqlite3`. Adjust installation paths if your server differs. This is an outbound MQTT worker, not a PHP page or a cron task that should be restarted every minute.

## 4. Update the existing Hostinger website

1. Back up the current deployed files, including root and `Build/.htaccess`.
2. Stop Play Mode. Build a fresh **Web** output from Unity. Keep the Gzip/decompression settings that already work with your server.
3. In the existing site's File Manager, overwrite the generated files inside `public_html/Build` and `public_html/TemplateData`. Upload any generated `StreamingAssets` files as well. Keep the folders and their `.htaccess` files.
4. Upload `guardian/web/guardian-context/` as `public_html/guardian-context/`. For a dashboard in a subfolder, place `guardian-context` beside that dashboard's `index.html`.
5. Upload the new Unity `index.html` last. Keep the build's loader/framework/data/wasm files together from the same build.
6. Purge Hostinger cache and test in Incognito. Do not clear browser site storage unnecessarily: it holds saved dashboard settings/last-known readings.
7. Verify the original demo, live well discovery, Settings, connection diagnostics, Insights and the historical map link.

Do **not** upload `.env`, `config.local.json`, `.venv`, Python source as a public executable endpoint, or the SQLite database into `public_html`. Only Unity's generated web files and `web/guardian-context` belong there. Your existing server configuration files need no replacement for Guardian.

## What results to expect

- Live values are available as soon as the sensor publishes valid telemetry.
- Trends/forecasts require at least 12 minute aggregates spanning six hours by default; genuine long-range outputs take representative longer history. Horizon is capped at twice the observed span and the configured maximum. A day-30 projection needs enough history and a suitable configured window.
- A major gap, an isolated latest spike, stale data or a retained-only history withholds a new forecast. This is expected, not a loading error.
- Positive depth slope means the water surface is getting deeper. A change in column-fill percentage is not a measurement of aquifer reserves.
- Default trend outputs are exploratory; simulated examples are illustrative. Residual bands are heuristic, not calibrated probabilities. The evidence panel includes holdout error/band coverage when enough points exist.
- Common-datum head interpolation requires explicit matching datum/aquifer metadata; otherwise a relative-column surface is used. TDS colours are estimated patterns, not a confirmed contamination plume.
- The optional pumping footprint is schematic and hypothetical. Abstraction volume can be computed without claiming a water-level response; area and specific yield are mandatory for that response.
- Basic scenario arithmetic remains available locally if the worker is offline. Up to 30 local scenarios are retained on this device/browser, scoped to the broker and account. A service reply confirms server-side persistence; local-only results are identified in the panel.

## Offline demonstration and tests

The existing ten-well/90-second animation remains unchanged. In demo mode, Guardian uses a **separate designed 30-day history**, clearly labelled illustrative; it does not infer a multi-week forecast from 90 seconds of samples. Generate the bundled examples again with:

```powershell
.\.venv\Scripts\python.exe -m guardian_core examples --output ..\unity\Assets\AquaTwin\Resources\GuardianExamples.json
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

In Unity, use **AquaTwin -> Validate Guardian**, plus the existing validation menus. Try Test demo -> Insights -> Time machine, move the horizon and choose View twin. For What-if, enter 2 m³/hour, 6 hours/day, 30%, 7 days: baseline is 12, intervention 8.4, avoided abstraction 25.2 m³. Expand the optional area/specific-yield inputs only to explore a hypothetical response.

## History, exports and backups

```powershell
.\.venv\Scripts\python.exe -m guardian_core export --db data/guardian.sqlite3 --output exports/telemetry.csv
.\.venv\Scripts\python.exe -m guardian_core backup --db data/guardian.sqlite3 --output backups/guardian-2026-10-26.sqlite3
.\.venv\Scripts\python.exe -m guardian_core analyze --db data/guardian.sqlite3 --config config.local.json
```

Choose a new backup filename each time. The backup command uses SQLite's backup API so a running WAL database is copied consistently; do not copy only the main database file while collection is writing. Backups/exports can contain well locations and should remain private. Model-run/raw history is retained; monitor disk use and schedule your own retention/backups according to deployment needs.

For an explicit simulated JSONL dataset, `replay file.jsonl --db data/replay.sqlite3` imports to a separate database and publishes nothing. Never replay synthetic telemetry into a physical well's production history.

## Troubleshooting

| Symptom | Check |
|---|---|
| Broker connected but zero raw packets | The sensor/Wokwi must actually run and publish to the same cluster/topic; starting HiveMQ alone is insufficient. |
| Raw readings work, no Guardian evidence | Worker process/logs; worker subscriptions and insight publish permissions; dashboard insight subscribe permissions. |
| Calculated locally / no reply | Worker running, dashboard request publish permission, worker request subscribe and reply publish permission, client clock. |
| Stale/insufficient evidence | Source timestamps, worker uptime, minimum coverage, retained flag and history gaps. Do not weaken gates merely to display a forecast. |
| Critical depth never crosses | No configured operating depth, stable/recovering trend, or crossing beyond the supported horizon. |
| Context map missing | Upload `guardian-context` beside the deployed `index.html`; EAD service/network availability. The UI shows an unavailable state rather than fake map data. |
| Old deployment still appears | Upload all files from one build, preserve `.htaccess`, purge Hostinger CDN cache and test a fresh browser session. |

## Contracts and sources

See `CONTRACTS.md` for MQTT fields, persistence and units. Historical map catalogue and legend were retrieved from the public EAD service on 26 October 2026:

- <https://edp.ead.ae/server/rest/services/Enviroportal/Groundwater/MapServer>
- <https://edp.ead.ae/server/rest/services/Enviroportal/Groundwater/MapServer/legend?f=pjson>
