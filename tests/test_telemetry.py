from datetime import datetime, timezone
import json

import pytest

from cloud_monitor.telemetry import (
    InvalidFieldError,
    InvalidJsonError,
    MissingFieldError,
    TopicMismatchError,
    parse_telemetry,
)


TOPIC = "aquatwin/well/WELL_WOKWI_01/telemetry"
TOPIC_FILTER = "aquatwin/well/+/telemetry"
VALID = {
    "well_id": "WELL_WOKWI_01",
    "location": {"lat": 24.453900, "lng": 54.377300},
    "depth_to_water_cm": 143.14,
    "water_column_height_cm": 356.86,
    "tds_ppm": 320.3,
    "status": "ONLINE",
    "timestamp": 1791700194,
}


def test_valid_current_payload() -> None:
    telemetry = parse_telemetry(json.dumps(VALID), TOPIC, TOPIC_FILTER)
    assert telemetry.well_id == "WELL_WOKWI_01"
    assert telemetry.latitude == 24.4539
    assert telemetry.longitude == 54.3773
    assert telemetry.depth_to_water_cm == 143.14
    assert telemetry.water_column_height_cm == 356.86
    assert telemetry.tds_ppm == 320.3
    assert telemetry.status == "ONLINE"
    assert telemetry.source_timestamp == 1791700194
    assert telemetry.received_at.tzinfo is not None


@pytest.mark.parametrize(
    "field",
    [
        "well_id",
        "location",
        "depth_to_water_cm",
        "water_column_height_cm",
        "tds_ppm",
        "status",
        "timestamp",
    ],
)
def test_missing_required_fields(field: str) -> None:
    payload = VALID.copy()
    payload.pop(field)
    with pytest.raises(MissingFieldError):
        parse_telemetry(json.dumps(payload), TOPIC, TOPIC_FILTER)


@pytest.mark.parametrize("field", ["lat", "lng"])
def test_missing_location_fields(field: str) -> None:
    payload = {**VALID, "location": VALID["location"].copy()}
    payload["location"].pop(field)
    with pytest.raises(MissingFieldError):
        parse_telemetry(json.dumps(payload), TOPIC, TOPIC_FILTER)


@pytest.mark.parametrize("payload", ["not json", "[]", b"\xff"])
def test_invalid_json(payload) -> None:
    with pytest.raises(InvalidJsonError):
        parse_telemetry(payload, TOPIC, TOPIC_FILTER)


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("well_id", 1),
        ("well_id", ""),
        ("location", "24.4,54.3"),
        ("depth_to_water_cm", "143.14"),
        ("depth_to_water_cm", -1),
        ("water_column_height_cm", -1),
        ("tds_ppm", None),
        ("tds_ppm", -1),
        ("status", ""),
        ("timestamp", 1791700194.5),
        ("timestamp", True),
        ("timestamp", -1),
    ],
)
def test_incorrect_data_types_or_ranges(field: str, value) -> None:
    payload = VALID.copy()
    payload[field] = value
    with pytest.raises(InvalidFieldError):
        parse_telemetry(json.dumps(payload), TOPIC, TOPIC_FILTER)


@pytest.mark.parametrize(
    ("field", "value"),
    [("lat", 90.1), ("lat", -90.1), ("lng", 180.1), ("lng", -180.1)],
)
def test_invalid_location_ranges(field: str, value: float) -> None:
    payload = {**VALID, "location": VALID["location"].copy()}
    payload["location"][field] = value
    with pytest.raises(InvalidFieldError):
        parse_telemetry(json.dumps(payload), TOPIC, TOPIC_FILTER)


def test_legacy_payload_is_rejected_by_frozen_contract() -> None:
    legacy = {"device": "well01", "distance_cm": 12.4, "tds_ppm": 342.5, "ph": 7.63}
    with pytest.raises(MissingFieldError):
        parse_telemetry(json.dumps(legacy), TOPIC, TOPIC_FILTER)


def test_topic_must_match_subscription() -> None:
    with pytest.raises(TopicMismatchError):
        parse_telemetry(json.dumps(VALID), TOPIC + "/other", TOPIC_FILTER)


def test_payload_well_id_must_match_topic_well_id() -> None:
    payload = {**VALID, "well_id": "WELL_OTHER_01"}
    with pytest.raises(TopicMismatchError):
        parse_telemetry(json.dumps(payload), TOPIC, TOPIC_FILTER)


def test_supplied_receipt_timestamp_is_preserved() -> None:
    received_at = datetime(2026, 10, 11, 12, 0, tzinfo=timezone.utc)
    telemetry = parse_telemetry(json.dumps(VALID), TOPIC, TOPIC_FILTER, received_at)
    assert telemetry.received_at is received_at


def test_naive_receipt_timestamp_is_rejected() -> None:
    with pytest.raises(InvalidFieldError):
        parse_telemetry(json.dumps(VALID), TOPIC, TOPIC_FILTER, datetime(2026, 1, 1))
