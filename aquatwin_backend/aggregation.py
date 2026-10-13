"""Validation and latest-reading aggregation for multi-well telemetry."""

from __future__ import annotations

import math
from typing import Iterable, Mapping


class SpatialInputError(ValueError):
    """Raised when a reading cannot safely be used for spatial interpolation."""


REQUIRED_FIELDS = {
    "well_id", "location", "depth_to_water_cm", "water_column_height_cm",
    "tds_ppm", "status", "timestamp",
}


def _finite_number(value: object, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise SpatialInputError(f"{label} must be a JSON number")
    number = float(value)
    if not math.isfinite(number):
        raise SpatialInputError(f"{label} must be finite")
    return number


def validate_reading(reading: Mapping[str, object], index: int = 0) -> dict[str, object]:
    missing = REQUIRED_FIELDS - set(reading)
    if missing:
        raise SpatialInputError(
            f"reading[{index}] missing field(s): {', '.join(sorted(missing))}"
        )

    well_id = reading["well_id"]
    if not isinstance(well_id, str) or not well_id.strip():
        raise SpatialInputError(f"reading[{index}].well_id must be a non-empty string")
    location = reading["location"]
    if not isinstance(location, Mapping):
        raise SpatialInputError(f"reading[{index}].location must be an object")
    if "lat" not in location or "lng" not in location:
        raise SpatialInputError(f"reading[{index}].location requires lat and lng")

    latitude = _finite_number(location["lat"], f"reading[{index}].location.lat")
    longitude = _finite_number(location["lng"], f"reading[{index}].location.lng")
    depth = _finite_number(
        reading["depth_to_water_cm"], f"reading[{index}].depth_to_water_cm"
    )
    water_column = _finite_number(
        reading["water_column_height_cm"],
        f"reading[{index}].water_column_height_cm",
    )
    tds = _finite_number(reading["tds_ppm"], f"reading[{index}].tds_ppm")
    timestamp = reading["timestamp"]
    status = reading["status"]

    if not -90 <= latitude <= 90:
        raise SpatialInputError(f"reading[{index}].location.lat is outside [-90, 90]")
    if not -180 <= longitude <= 180:
        raise SpatialInputError(f"reading[{index}].location.lng is outside [-180, 180]")
    if depth < 0 or water_column < 0 or tds < 0:
        raise SpatialInputError(
            f"reading[{index}] depth, water column, and TDS cannot be negative"
        )
    if isinstance(timestamp, bool) or not isinstance(timestamp, int) or timestamp < 0:
        raise SpatialInputError(f"reading[{index}].timestamp must be a non-negative integer")
    if not isinstance(status, str) or not status.strip():
        raise SpatialInputError(f"reading[{index}].status must be a non-empty string")

    return {
        "well_id": well_id.strip(),
        "location": {"lat": latitude, "lng": longitude},
        "depth_to_water_cm": depth,
        "water_column_height_cm": water_column,
        "tds_ppm": tds,
        "status": status.strip(),
        "timestamp": timestamp,
    }


def aggregate_latest_readings(
    readings: Iterable[Mapping[str, object]],
) -> list[dict[str, object]]:
    """Validate readings and keep the newest packet for each well_id."""
    latest: dict[str, dict[str, object]] = {}
    for index, raw in enumerate(readings):
        reading = validate_reading(raw, index)
        current = latest.get(str(reading["well_id"]))
        if current is None or int(reading["timestamp"]) > int(current["timestamp"]):
            latest[str(reading["well_id"])] = reading
        elif int(reading["timestamp"]) == int(current["timestamp"]) and reading != current:
            raise SpatialInputError(
                f"conflicting duplicate packets for {reading['well_id']} at "
                f"timestamp {reading['timestamp']}"
            )
    if not latest:
        raise SpatialInputError("at least one complete well reading is required")
    return [latest[well_id] for well_id in sorted(latest)]


def merge_duplicate_locations(
    readings: Iterable[Mapping[str, object]],
) -> list[dict[str, object]]:
    """Merge co-located wells by averaging values before IDW."""
    groups: dict[tuple[float, float], list[Mapping[str, object]]] = {}
    for reading in readings:
        location = reading["location"]
        key = (round(float(location["lat"]), 10), round(float(location["lng"]), 10))
        groups.setdefault(key, []).append(reading)

    points: list[dict[str, object]] = []
    for (latitude, longitude), group in sorted(groups.items()):
        count = len(group)
        points.append(
            {
                "well_ids": [str(item["well_id"]) for item in group],
                "location": {"lat": latitude, "lng": longitude},
                "depth_to_water_cm": sum(
                    float(item["depth_to_water_cm"]) for item in group
                ) / count,
                "tds_ppm": sum(float(item["tds_ppm"]) for item in group) / count,
            }
        )
    return points
