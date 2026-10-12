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


TOPIC = "digitaltwin/well01/sensors"
VALID = {"device": "well01", "distance_cm": 12.4, "tds_ppm": 342.5, "ph": 7.63}


def test_valid_firmware_payload() -> None:
    telemetry = parse_telemetry(json.dumps(VALID), TOPIC, TOPIC)
    assert telemetry.device == "well01"
    assert telemetry.distance_cm == 12.4
    assert telemetry.tds_ppm == 342.5
    assert telemetry.ph == 7.63
    assert telemetry.received_at.tzinfo is not None


@pytest.mark.parametrize("field", ["device", "distance_cm", "tds_ppm"])
def test_missing_required_fields(field: str) -> None:
    payload = VALID.copy()
    payload.pop(field)
    with pytest.raises(MissingFieldError):
        parse_telemetry(json.dumps(payload), TOPIC, TOPIC)


def test_ph_is_optional() -> None:
    payload = VALID.copy()
    payload.pop("ph")
    assert parse_telemetry(json.dumps(payload), TOPIC, TOPIC).ph is None


@pytest.mark.parametrize("payload", ["not json", "[]", b"\xff"])
def test_invalid_json(payload) -> None:
    with pytest.raises(InvalidJsonError):
        parse_telemetry(payload, TOPIC, TOPIC)


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("device", 1),
        ("device", ""),
        ("distance_cm", "12.4"),
        ("distance_cm", True),
        ("distance_cm", -1),
        ("tds_ppm", None),
        ("tds_ppm", -1),
        ("ph", "7.2"),
        ("ph", 14.1),
    ],
)
def test_incorrect_data_types_or_ranges(field: str, value) -> None:
    payload = VALID.copy()
    payload[field] = value
    with pytest.raises(InvalidFieldError):
        parse_telemetry(json.dumps(payload), TOPIC, TOPIC)


def test_topic_must_match_subscription() -> None:
    with pytest.raises(TopicMismatchError):
        parse_telemetry(json.dumps(VALID), TOPIC + "/other", TOPIC)


def test_supplied_timestamp_is_preserved() -> None:
    timestamp = datetime(2026, 10, 11, 12, 0, tzinfo=timezone.utc)
    telemetry = parse_telemetry(json.dumps(VALID), TOPIC, TOPIC, timestamp)
    assert telemetry.received_at is timestamp


def test_naive_timestamp_is_rejected() -> None:
    with pytest.raises(InvalidFieldError):
        parse_telemetry(json.dumps(VALID), TOPIC, TOPIC, datetime(2026, 1, 1))

