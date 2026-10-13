import numpy as np
import pytest

from aquatwin_backend.idw import build_spatial_grid, idw_interpolate


def reading(well_id, lat, lng, depth, tds, timestamp=1):
    return {
        "well_id": well_id,
        "location": {"lat": lat, "lng": lng},
        "depth_to_water_cm": depth,
        "water_column_height_cm": 500.0 - depth,
        "tds_ppm": tds,
        "status": "ONLINE",
        "timestamp": timestamp,
    }


def test_zero_distance_returns_exact_measured_value() -> None:
    result = idw_interpolate(
        np.array([0.0, 10.0]),
        np.array([0.0, 0.0]),
        np.array([100.0, 200.0]),
        np.array([0.0]),
        np.array([0.0]),
    )
    assert result[0] == 100.0


def test_symmetric_midpoint_matches_hand_calculated_idw() -> None:
    result = idw_interpolate(
        np.array([0.0, 10.0]),
        np.array([0.0, 0.0]),
        np.array([100.0, 200.0]),
        np.array([5.0]),
        np.array([0.0]),
        power=2,
    )
    assert result[0] == pytest.approx(150.0)


def test_asymmetric_point_matches_manual_equation() -> None:
    result = idw_interpolate(
        np.array([0.0, 10.0]),
        np.array([0.0, 0.0]),
        np.array([100.0, 200.0]),
        np.array([2.0]),
        np.array([0.0]),
        power=2,
    )
    expected = (100.0 / 2.0**2 + 200.0 / 8.0**2) / (
        1.0 / 2.0**2 + 1.0 / 8.0**2
    )
    assert result[0] == pytest.approx(expected)


def test_grid_is_flat_row_major_and_has_separate_depth_and_tds_values() -> None:
    wells = [
        reading("W1", 24.0, 54.0, 100, 200),
        reading("W2", 24.1, 54.1, 200, 600),
    ]
    result = build_spatial_grid(wells, rows=3, columns=4)
    grid = result["grid"]
    assert len(grid["depth_to_water_cm"]) == 12
    assert len(grid["tds_ppm"]) == 12
    assert grid["depth_to_water_cm"][0] == 100.0
    assert grid["tds_ppm"][0] == 200.0
    assert grid["depth_to_water_cm"][-1] == 200.0
    assert grid["tds_ppm"][-1] == 600.0


def test_depth_surface_is_not_mislabelled_as_groundwater_elevation() -> None:
    result = build_spatial_grid([reading("W1", 24.0, 54.0, 100, 200)], 2, 2)
    reference = result["surface_reference"]
    assert reference["metric"] == "depth_to_water_below_local_wellhead"
    assert reference["unit"] == "cm"
    assert reference["is_groundwater_elevation"] is False


def test_duplicate_coordinates_do_not_receive_double_weight() -> None:
    wells = [
        reading("W1", 24.0, 54.0, 100, 200),
        reading("W2", 24.0, 54.0, 140, 400),
    ]
    result = build_spatial_grid(wells, rows=2, columns=2)
    assert result["source"]["latest_well_count"] == 2
    assert result["source"]["spatial_point_count"] == 1
    assert result["grid"]["depth_to_water_cm"] == [120.0] * 4
    assert result["grid"]["tds_ppm"] == [300.0] * 4


@pytest.mark.parametrize(
    ("rows", "columns", "power"),
    [(1, 2, 2.0), (2, 1, 2.0), (2, 2, 0.0), (2, 2, -1.0)],
)
def test_invalid_grid_configuration_is_rejected(rows, columns, power) -> None:
    with pytest.raises(ValueError):
        build_spatial_grid(
            [reading("W1", 24.0, 54.0, 100, 200)], rows, columns, power
        )
