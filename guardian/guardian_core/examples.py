"""Export separate, explicitly illustrative long-history examples. Never publish raw data."""
import json
from pathlib import Path
from .analytics import analyze
from .store import Store


def export_examples(output):
    now = 1793016000  # fixed reference epoch; UI labels this separate designed scenario
    store = Store(":memory:", {"window_days": 31, "max_horizon_days": 60,
        "wells": {f"DEMO_{i:03}": {"source": "simulated", "critical_depth_cm": 360} for i in range(1, 11)}})
    results = []
    for i in range(1, 11):
        well = f"DEMO_{i:03}"
        for h in range(30*24+1):
            day = h/24
            depth = 220 + (2 if i%3 else -.5)*day
            data = {"well_id": well, "source": "simulated", "location": {"lat": 24.45+i*.001, "lng": 54.37+i*.001},
                "depth_to_water_cm": depth, "water_column_height_cm": 500-depth, "tds_ppm": 330+4*day,
                "status": "ONLINE", "timestamp": now-30*86400+h*3600}
            store.ingest(json.dumps(data), f"aquatwin/well/{well}/telemetry", received_at=data["timestamp"], replay=True)
        result = analyze(store, well, now)
        result["message"] = "Separate designed 30-day history, not inferred from the 90-second animation. " + result["message"]
        results.append(result)
    output = Path(output); output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps({"insights": results}, indent=2, allow_nan=False), encoding="utf-8")
    store.close()
