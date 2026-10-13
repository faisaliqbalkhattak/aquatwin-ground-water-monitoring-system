import asyncio
from datetime import datetime, timezone

import httpx

from aquatwin_backend.api import create_app
from aquatwin_backend.storage import TelemetryStore
from cloud_monitor.telemetry import Telemetry


def insert_well(store: TelemetryStore, well_id: str, lat: float, lng: float) -> None:
    store.insert(
        Telemetry(
            well_id=well_id,
            latitude=lat,
            longitude=lng,
            depth_to_water_cm=140.0,
            water_column_height_cm=360.0,
            tds_ppm=320.0,
            status="ONLINE",
            source_timestamp=1791700194,
            topic=f"aquatwin/well/{well_id}/telemetry",
            received_at=datetime.now(timezone.utc),
        )
    )


async def get(app, path: str) -> httpx.Response:
    transport = httpx.ASGITransport(app=app)
    async with httpx.AsyncClient(transport=transport, base_url="http://test") as client:
        return await client.get(path)


def test_empty_backend_is_healthy_and_waits_for_data(tmp_path) -> None:
    store = TelemetryStore(tmp_path / "telemetry.db")
    app = create_app(store, start_mqtt=False)
    assert asyncio.run(get(app, "/health")).json()["status"] == "ok"
    terrain = asyncio.run(get(app, "/api/terrain-matrix")).json()
    assert terrain["ready"] is False
    assert terrain["source"]["latest_well_count"] == 0
    assert "Live Multi-Well IDW" in asyncio.run(get(app, "/")).text


def test_rest_returns_dynamic_grid_shape(tmp_path) -> None:
    store = TelemetryStore(tmp_path / "telemetry.db")
    insert_well(store, "W1", 24.45, 54.37)
    insert_well(store, "W2", 24.46, 54.38)
    app = create_app(store, start_mqtt=False)

    live = asyncio.run(get(app, "/api/wells/live")).json()
    assert live["count"] == 2
    assert all(well["is_stale"] is False for well in live["wells"])

    terrain = asyncio.run(get(app, "/api/terrain-matrix")).json()
    assert terrain["ready"] is True
    assert len(terrain["grid"]["depth_to_water_cm"]) == 48 * 48
    assert len(terrain["grid"]["tds_ppm"]) == 48 * 48
