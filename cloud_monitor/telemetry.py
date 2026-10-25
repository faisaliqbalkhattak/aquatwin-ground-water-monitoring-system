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
    well_id: str
    latitude: float
    longitude: float
    depth_to_water_cm: float
    water_column_height_cm: float
    tds_ppm: float
    status: str
    source_timestamp: int
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


def _text(data: dict[str, Any], name: str) -> str:
    if name not in data:
        raise MissingFieldError(f"Missing required field: {name}")
    value = data[name]
    if not isinstance(value, str) or not value.strip():
        raise InvalidFieldError(f"{name} must be a non-empty string.")
    return value.strip()


def _received_at(value: datetime | None) -> datetime:
    timestamp = value or datetime.now(timezone.utc)
    if timestamp.tzinfo is None or timestamp.utcoffset() is None:
        raise InvalidFieldError("received_at must include timezone information.")
    return timestamp


def _parse_current_schema(
    data: dict[str, Any], topic: str, received_at: datetime | None
) -> Telemetry:
    expected_fields = {
        "well_id",
        "location",
        "depth_to_water_cm",
        "water_column_height_cm",
        "tds_ppm",
        "status",
        "timestamp",
    }
    missing = expected_fields - set(data)
    if missing:
        raise MissingFieldError(
            "Missing required field(s): " + ", ".join(sorted(missing))
        )
    unexpected = set(data) - expected_fields
    if unexpected:
        raise InvalidFieldError(
            "Unexpected field(s) in frozen schema: " + ", ".join(sorted(unexpected))
        )

    well_id = _text(data, "well_id")
    status = _text(data, "status")

    location = data.get("location")
    if location is None:
        raise MissingFieldError("Missing required field: location")
    if not isinstance(location, dict):
        raise InvalidFieldError("location must be a JSON object.")
    missing_location = {"lat", "lng"} - set(location)
    if missing_location:
        raise MissingFieldError(
            "Missing location field(s): " + ", ".join(sorted(missing_location))
        )
    if set(location) != {"lat", "lng"}:
        raise InvalidFieldError("location must contain exactly lat and lng.")
    latitude = _number(location, "lat")
    longitude = _number(location, "lng")
    depth = _number(data, "depth_to_water_cm")
    water_column = _number(data, "water_column_height_cm")
    tds = _number(data, "tds_ppm")
    assert latitude is not None and longitude is not None
    assert depth is not None and water_column is not None and tds is not None

    if not -90 <= latitude <= 90:
        raise InvalidFieldError("location.lat must be between -90 and 90.")
    if not -180 <= longitude <= 180:
        raise InvalidFieldError("location.lng must be between -180 and 180.")
    if depth < 0:
        raise InvalidFieldError("depth_to_water_cm cannot be negative.")
    if water_column < 0:
        raise InvalidFieldError("water_column_height_cm cannot be negative.")
    if tds < 0:
        raise InvalidFieldError("tds_ppm cannot be negative.")

    if "timestamp" not in data:
        raise MissingFieldError("Missing required field: timestamp")
    source_timestamp = data["timestamp"]
    if isinstance(source_timestamp, bool) or not isinstance(source_timestamp, int):
        raise InvalidFieldError("timestamp must be a Unix integer.")
    if source_timestamp < 0:
        raise InvalidFieldError("timestamp cannot be negative.")
    try:
        datetime.fromtimestamp(source_timestamp, timezone.utc)
    except (OverflowError, OSError, ValueError) as exc:
        raise InvalidFieldError("timestamp is outside the supported Unix range.") from exc

    return Telemetry(
        well_id,
        latitude,
        longitude,
        depth,
        water_column,
        tds,
        status,
        source_timestamp,
        topic,
        _received_at(received_at),
    )


def parse_telemetry(
    payload: bytes | str,
    topic: str,
    expected_topic: str,
    received_at: datetime | None = None,
) -> Telemetry:
    """Decode the frozen schema and enforce topic/well identity consistency."""
    expected_parts = expected_topic.split("/")
    topic_parts = topic.split("/")
    matches_filter = (
        len(expected_parts) == len(topic_parts) and
        all(expected == "+" or expected == actual
            for expected, actual in zip(expected_parts, topic_parts))
    )
    if not matches_filter:
        raise TopicMismatchError(f"Unexpected topic: {topic}")
    if len(topic_parts) != 4 or topic_parts[:2] != ["aquatwin", "well"] or \
            topic_parts[3] != "telemetry" or not topic_parts[2]:
        raise TopicMismatchError(f"Topic violates frozen contract: {topic}")
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

    telemetry = _parse_current_schema(data, topic, received_at)
    if telemetry.well_id != topic_parts[2]:
        raise TopicMismatchError(
            "Payload well_id must match the well_id segment in the MQTT topic."
        )
    return telemetry
