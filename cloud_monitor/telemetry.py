"""AquaTwin firmware payload validation."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import json
import math
from typing import Any


class TelemetryError(ValueError):
    """Base error for payloads that cannot be accepted as telemetry."""


class InvalidJsonError(TelemetryError):
    pass


class MissingFieldError(TelemetryError):
    pass


class InvalidFieldError(TelemetryError):
    pass


class TopicMismatchError(TelemetryError):
    pass


@dataclass(frozen=True)
class Telemetry:
    device: str
    distance_cm: float
    tds_ppm: float
    ph: float | None
    topic: str
    received_at: datetime


def _number(data: dict[str, Any], name: str, *, required: bool = True) -> float | None:
    if name not in data:
        if required:
            raise MissingFieldError(f"Missing required field: {name}")
        return None
    value = data[name]
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise InvalidFieldError(f"{name} must be a JSON number.")
    value = float(value)
    if not math.isfinite(value):
        raise InvalidFieldError(f"{name} must be finite.")
    return value


def parse_telemetry(
    payload: bytes | str,
    topic: str,
    expected_topic: str,
    received_at: datetime | None = None,
) -> Telemetry:
    """Decode and validate the actual production firmware schema.

    Firmware currently sends pH on every message. It remains optional here so an
    unavailable pH sensor does not hide otherwise useful distance/TDS telemetry.
    """
    if topic != expected_topic:
        raise TopicMismatchError(f"Unexpected topic: {topic}")
    if isinstance(payload, bytes):
        try:
            payload = payload.decode("utf-8")
        except UnicodeDecodeError as exc:
            raise InvalidJsonError("Payload is not valid UTF-8.") from exc
    try:
        data = json.loads(payload)
    except (json.JSONDecodeError, TypeError) as exc:
        raise InvalidJsonError("Payload is not valid JSON.") from exc
    if not isinstance(data, dict):
        raise InvalidJsonError("Telemetry JSON must be an object.")

    device = data.get("device")
    if not isinstance(device, str) or not device.strip():
        if "device" not in data:
            raise MissingFieldError("Missing required field: device")
        raise InvalidFieldError("device must be a non-empty string.")

    distance = _number(data, "distance_cm")
    tds = _number(data, "tds_ppm")
    ph = _number(data, "ph", required=False)
    assert distance is not None and tds is not None
    if distance < 0:
        raise InvalidFieldError("distance_cm cannot be negative.")
    if tds < 0:
        raise InvalidFieldError("tds_ppm cannot be negative.")
    if ph is not None and not 0 <= ph <= 14:
        raise InvalidFieldError("ph must be between 0 and 14.")

    timestamp = received_at or datetime.now(timezone.utc)
    if timestamp.tzinfo is None or timestamp.utcoffset() is None:
        raise InvalidFieldError("received_at must include timezone information.")
    return Telemetry(device.strip(), distance, tds, ph, topic, timestamp)

