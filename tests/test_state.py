from datetime import datetime, timedelta, timezone

from cloud_monitor.state import MonitorState
from cloud_monitor.telemetry import Telemetry


def sample(received_at: datetime) -> Telemetry:
    return Telemetry("well01", 12.0, 300.0, 7.0, "digitaltwin/well01/sensors", received_at)


def test_no_message_is_stale() -> None:
    assert MonitorState(10).snapshot().stale is True


def test_recent_message_is_not_stale() -> None:
    now = datetime.now(timezone.utc)
    state = MonitorState(10)
    state.record(sample(now - timedelta(seconds=9)))
    snapshot = state.snapshot(now)
    assert snapshot.stale is False
    assert snapshot.message_count == 1


def test_old_message_is_stale() -> None:
    now = datetime.now(timezone.utc)
    state = MonitorState(10)
    state.record(sample(now - timedelta(seconds=11)))
    assert state.snapshot(now).stale is True


def test_invalid_message_count_does_not_replace_last_good_data() -> None:
    now = datetime.now(timezone.utc)
    state = MonitorState(10)
    telemetry = sample(now)
    state.record(telemetry)
    state.record_invalid("bad json")
    snapshot = state.snapshot(now)
    assert snapshot.telemetry is telemetry
    assert snapshot.invalid_count == 1
    assert snapshot.message_count == 1

