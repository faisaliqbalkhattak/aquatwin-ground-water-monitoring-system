# Validation record — 26 October 2026

## Completed

- Unity 6000.3.2f1 C# compilation with Editor symbols: passed.
- C# compilation with WebGL symbols, excluding Editor scripts: passed. This checks source compatibility; it is not an IL2CPP/WebGL player build.
- 274 pure C# checks passed: Guardian 13, startup/recovery 25, fleet 126, MQTT 53, settings 27, demo 30.
- 15 JavaScript browser MQTT bridge checks passed.
- 19 Python backend tests passed, including restart persistence, 37 dynamic well identities, stale/retained/future timestamps, duplicates/conflicts, minimum evidence, anomaly handling, configured operating limits, forecast examples and scenario arithmetic/request routing.
- CLI simulated replay, CSV export and SQLite online-backup API verified with 15 rows.
- Paho 2.1 callback, verified TLS and WebSocket setup exercised without contacting the production broker.
- Saved SampleScene checked for valid local object references, exactly one modern dashboard, and absence of the retired visual roots.
- Historical context page loaded in a real browser against the public EAD service; historical layer switching and zoom exercised.
- Repository whitespace check passed (pre-existing line-ending notices may remain).

## Still required on the deployment machine

The Editor process had no visible window during this validation, so the new layout, preview rendering, Play/Stop lifecycle and pointer interactions were not visually verified in Unity. Open SampleScene, run the AquaTwin validation menus, then check Play -> demo -> Insights -> Time machine -> What-if -> Stop -> Play.

No full WebGL player build, fresh deployment to Hostinger, live Guardian worker/publisher integration, broker permission changes or production endurance test was performed for this update. These are not claimed by the source/compiler checks. Test live discovery and a Guardian round trip with a running sensor and worker before replacing the public build.

The service must collect sufficient valid history before forecasting. Empty/insufficient/stale results are deliberately disclosed; illustrative demo results are separate from live evidence. Review operational thresholds and physical metadata for each real deployment.

## Follow-up Editor check — 26 October 2026

Unity became available. Verified the modern Edit Mode preview, Play Mode, restored last-known wells, ten-well animated demo, Evidence drawer, Time Machine chart/ghost projection, and What-if inputs/calculation (25.2 m³ avoided over seven days). Corrected render-texture detach-before-release, a static Reset method name colliding with Unity's Editor callback, summary compression and drawer/slider spacing. Recompiled successfully. Console showed zero errors/warnings after the fixes. Stopped Play Mode and confirmed the modern preview returns.

Live MQTT authenticated, but WELL_WOKWI_01 reported an out-of-order source timestamp and no fresh live readings were verified. This check does not validate current publisher clock/data or a live Guardian worker. Full WebGL build/deployment remains outstanding.

## Twin focus and Editor startup follow-up

Added Maximize/Restore and Escape restoration using the same scene and telemetry. Editor/WebGL source compilation passed. Button presence observed; automated pointer activation did not conclusively verify expansion.
Removed continuous RepaintAllViews/unconditional preview rendering, delayed construction until asset updates settle, prevented runtime construction in Edit-mode Awake/OnEnable, and added reload/quit/scene-close cleanup and a failed-preview retry guard. No current crash dump or reproducible abrupt exit was found; cold-start/endurance verification remains outstanding.
