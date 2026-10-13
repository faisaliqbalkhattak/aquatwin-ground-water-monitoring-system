"""Event-driven FastAPI endpoints consumed by Unity and the live dashboard."""

from __future__ import annotations

from contextlib import asynccontextmanager
from datetime import datetime, timezone
import asyncio
import os
from pathlib import Path

from fastapi import FastAPI, Query, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import HTMLResponse

from cloud_monitor.config import MqttSettings

from .idw import build_empty_spatial_grid, build_spatial_grid
from .ingestion import MqttIngestionService
from .storage import TelemetryStore
from .updates import UpdateBroker


STATIC_DIRECTORY = Path(__file__).with_name("static")


def _positive_int(environment_name: str, default: int, minimum: int = 2) -> int:
    value = int(os.getenv(environment_name, str(default)))
    if value < minimum:
        raise ValueError(f"{environment_name} must be at least {minimum}")
    return value


def create_app(
    store: TelemetryStore | None = None,
    *,
    start_mqtt: bool = True,
    env_file: str | Path = ".env",
) -> FastAPI:
    telemetry_store = store or TelemetryStore(
        os.getenv("AQUATWIN_DB_PATH", "data/aquatwin.db")
    )
    rows = _positive_int("AQUATWIN_GRID_ROWS", 48)
    columns = _positive_int("AQUATWIN_GRID_COLUMNS", 48)
    power = float(os.getenv("AQUATWIN_GRID_POWER", "2"))
    stale_seconds = float(os.getenv("AQUATWIN_STALE_SECONDS", "10"))
    if power <= 0 or stale_seconds <= 0:
        raise ValueError("AQUATWIN_GRID_POWER and AQUATWIN_STALE_SECONDS must be positive")

    updates = UpdateBroker()
    ingestion: MqttIngestionService | None = None

    def terrain() -> dict[str, object]:
        wells = telemetry_store.latest_wells()
        document = (
            build_spatial_grid(wells, rows=rows, columns=columns, power=power)
            if wells
            else build_empty_spatial_grid(rows, columns, power)
        )
        if wells:
            last_received = max(
                datetime.fromisoformat(str(well["received_at"])) for well in wells
            )
            age_seconds = max(
                0.0, (datetime.now(timezone.utc) - last_received).total_seconds()
            )
            document["stream"] = {
                "last_received_at": last_received.isoformat(),
                "age_seconds": round(age_seconds, 3),
                "is_stale": age_seconds > stale_seconds,
                "stale_after_seconds": stale_seconds,
            }
        else:
            document["stream"] = {
                "last_received_at": None,
                "age_seconds": None,
                "is_stale": True,
                "stale_after_seconds": stale_seconds,
            }
        document["update_sequence"] = updates.version
        return document

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        nonlocal ingestion
        if start_mqtt:
            loop = asyncio.get_running_loop()
            ingestion = MqttIngestionService(
                MqttSettings.from_env(env_file),
                telemetry_store,
                on_stored=lambda: updates.publish_threadsafe(loop),
            )
            ingestion.start()
            app.state.ingestion = ingestion
        yield
        if ingestion is not None:
            ingestion.stop()

    app = FastAPI(title="AquaTwin Backend API", version="1.0.0", lifespan=lifespan)
    app.state.store = telemetry_store
    app.state.updates = updates
    app.state.terrain = terrain
    app.state.ingestion = None
    app.add_middleware(
        CORSMiddleware,
        allow_origins=["*"],
        allow_methods=["GET"],
        allow_headers=["*"],
    )

    @app.get("/", response_class=HTMLResponse)
    async def dashboard() -> str:
        return (STATIC_DIRECTORY / "live_dashboard.html").read_text(encoding="utf-8")

    @app.get("/health")
    async def health() -> dict[str, object]:
        mqtt_status = ingestion.snapshot() if ingestion is not None else {
            "connected": False,
            "mode": "disabled_for_test",
        }
        return {
            "status": "ok",
            "time": datetime.now(timezone.utc).isoformat(),
            "mqtt": mqtt_status,
            "well_count": len(telemetry_store.latest_wells()),
            "update_sequence": updates.version,
        }

    @app.get("/api/wells/live")
    async def live_wells() -> dict[str, object]:
        now = datetime.now(timezone.utc)
        wells = telemetry_store.latest_wells()
        for well in wells:
            received = datetime.fromisoformat(str(well["received_at"]))
            age = max(0.0, (now - received).total_seconds())
            well["age_seconds"] = round(age, 3)
            well["is_stale"] = age > stale_seconds
        return {
            "count": len(wells),
            "stale_after_seconds": stale_seconds,
            "wells": wells,
        }

    @app.get("/api/wells/{well_id}/history")
    async def well_history(
        well_id: str,
        limit: int = Query(default=100, ge=1, le=1000),
    ) -> dict[str, object]:
        samples = telemetry_store.history(well_id, limit)
        return {"well_id": well_id, "count": len(samples), "samples": samples}

    @app.get("/api/terrain-matrix")
    async def terrain_matrix() -> dict[str, object]:
        return terrain()

    @app.websocket("/ws/terrain-matrix")
    async def terrain_websocket(websocket: WebSocket) -> None:
        await websocket.accept()
        queue = updates.subscribe()
        try:
            await websocket.send_json(terrain())
            while True:
                try:
                    await asyncio.wait_for(queue.get(), timeout=15)
                except TimeoutError:
                    pass
                await websocket.send_json(terrain())
        except (WebSocketDisconnect, RuntimeError):
            return
        finally:
            updates.unsubscribe(queue)

    return app


app = create_app()
