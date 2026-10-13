from datetime import datetime, timezone

from aquatwin_backend.storage import TelemetryStore
from cloud_monitor.telemetry import Telemetry


def telemetry(well_id: str, timestamp: int, depth: float) -> Telemetry:
    return Telemetry(
        well_id=well_id,
        latitude=24.45,
        longitude=54.37,
        depth_to_water_cm=depth,
        water_column_height_cm=500.0 - depth,
        tds_ppm=320.0,
        status="ONLINE",
        source_timestamp=timestamp,
        topic=f"aquatwin/well/{well_id}/telemetry",
        received_at=datetime.now(timezone.utc),
    )


def test_store_keeps_history_and_returns_latest_per_well(tmp_path) -> None:
    store = TelemetryStore(tmp_path / "telemetry.db")
    store.insert(telemetry("W1", 1, 100))
    store.insert(telemetry("W2", 1, 130))
    store.insert(telemetry("W1", 2, 110))

    latest = store.latest_wells()
    assert [well["well_id"] for well in latest] == ["W1", "W2"]
    assert latest[0]["depth_to_water_cm"] == 110
    assert [sample["timestamp"] for sample in store.history("W1")] == [1, 2]


def test_late_arriving_older_packet_does_not_replace_latest_value(tmp_path) -> None:
    store = TelemetryStore(tmp_path / "telemetry.db")
    store.insert(telemetry("W1", 20, 120))
    store.insert(telemetry("W1", 10, 100))
    assert store.latest_wells()[0]["timestamp"] == 20
    assert store.latest_wells()[0]["depth_to_water_cm"] == 120
