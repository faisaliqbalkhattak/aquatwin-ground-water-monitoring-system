import pytest

from mock_publisher import MOCK_WELLS, mock_payload


def test_mock_payload_matches_firmware_schema_and_is_labelled() -> None:
    payload = mock_payload(0)
    assert set(payload) == {
        "well_id",
        "location",
        "depth_to_water_cm",
        "water_column_height_cm",
        "tds_ppm",
        "status",
        "timestamp",
    }
    assert payload["well_id"] == "WELL_MOCK_01"
    assert payload["depth_to_water_cm"] + payload["water_column_height_cm"] == pytest.approx(500.0)


def test_mock_wells_have_unique_ids_and_locations() -> None:
    packets = [mock_payload(0, index) for index in range(len(MOCK_WELLS))]
    assert len({packet["well_id"] for packet in packets}) == 8
    assert len({tuple(packet["location"].values()) for packet in packets}) == 8
