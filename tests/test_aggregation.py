import pytest

from aquatwin_backend.aggregation import (
    SpatialInputError,
    aggregate_latest_readings,
    merge_duplicate_locations,
    validate_reading,
)


def reading(well_id="W1", timestamp=1, lat=24.4, lng=54.3, depth=100.0, tds=300.0):
    return {
        "well_id": well_id,
        "location": {"lat": lat, "lng": lng},
        "depth_to_water_cm": depth,
        "water_column_height_cm": 500.0 - depth,
        "tds_ppm": tds,
        "status": "ONLINE",
        "timestamp": timestamp,
    }


def test_latest_reading_is_kept_for_each_well() -> None:
    result = aggregate_latest_readings(
        [reading(timestamp=1, depth=100), reading(timestamp=2, depth=125)]
    )
    assert len(result) == 1
    assert result[0]["depth_to_water_cm"] == 125


def test_conflicting_same_timestamp_packet_is_rejected() -> None:
    with pytest.raises(SpatialInputError, match="conflicting duplicate"):
        aggregate_latest_readings(
            [reading(timestamp=2, depth=100), reading(timestamp=2, depth=125)]
        )


def test_missing_measurement_is_rejected() -> None:
    packet = reading()
    packet.pop("tds_ppm")
    with pytest.raises(SpatialInputError, match="tds_ppm"):
        validate_reading(packet)


@pytest.mark.parametrize(
    ("lat", "lng"),
    [(90.1, 54.3), (-90.1, 54.3), (24.4, 180.1), (24.4, -180.1)],
)
def test_invalid_coordinates_are_rejected(lat: float, lng: float) -> None:
    with pytest.raises(SpatialInputError):
        validate_reading(reading(lat=lat, lng=lng))


def test_non_finite_and_negative_readings_are_rejected() -> None:
    with pytest.raises(SpatialInputError):
        validate_reading(reading(depth=float("nan")))
    with pytest.raises(SpatialInputError):
        validate_reading(reading(tds=-1))


def test_duplicate_locations_are_merged_by_mean() -> None:
    points = merge_duplicate_locations(
        [reading("W1", depth=100, tds=200), reading("W2", depth=140, tds=400)]
    )
    assert len(points) == 1
    assert points[0]["well_ids"] == ["W1", "W2"]
    assert points[0]["depth_to_water_cm"] == 120
    assert points[0]["tds_ppm"] == 300
