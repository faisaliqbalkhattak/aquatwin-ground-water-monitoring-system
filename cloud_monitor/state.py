"""Thread-safe monitoring state and stale-data detection."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from threading import Lock

from .telemetry import Telemetry


@dataclass(frozen=True)
class MonitorSnapshot:
    connection: str
    connected: bool
    telemetry: Telemetry | None
    message_count: int
    invalid_count: int
    age_seconds: float | None
    stale: bool
    last_error: str | None


class MonitorState:
    def __init__(self, stale_timeout: float) -> None:
        if stale_timeout <= 0:
            raise ValueError("stale_timeout must be greater than zero")
        self.stale_timeout = stale_timeout
        self._lock = Lock()
        self._connection = "STARTING"
        self._connected = False
        self._telemetry: Telemetry | None = None
        self._message_count = 0
        self._invalid_count = 0
        self._last_error: str | None = None

    def set_connection(self, status: str, connected: bool) -> None:
        with self._lock:
            self._connection = status
            self._connected = connected

    def record(self, telemetry: Telemetry) -> None:
        with self._lock:
            self._telemetry = telemetry
            self._message_count += 1
            self._last_error = None

    def record_invalid(self, error: str) -> None:
        with self._lock:
            self._invalid_count += 1
            self._last_error = error

    def snapshot(self, now: datetime | None = None) -> MonitorSnapshot:
        now = now or datetime.now(timezone.utc)
        if now.tzinfo is None or now.utcoffset() is None:
            raise ValueError("now must include timezone information")
        with self._lock:
            telemetry = self._telemetry
            age = None if telemetry is None else max(
                0.0, (now - telemetry.received_at).total_seconds()
            )
            return MonitorSnapshot(
                connection=self._connection,
                connected=self._connected,
                telemetry=telemetry,
                message_count=self._message_count,
                invalid_count=self._invalid_count,
                age_seconds=age,
                stale=age is None or age > self.stale_timeout,
                last_error=self._last_error,
            )

