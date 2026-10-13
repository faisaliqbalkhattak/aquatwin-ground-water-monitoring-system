"""Validated Inverse Distance Weighting for AquaTwin spatial grids."""

from __future__ import annotations

from datetime import datetime, timezone
from typing import Mapping, Sequence

import numpy as np

from .aggregation import aggregate_latest_readings, merge_duplicate_locations


EARTH_RADIUS_METERS = 6_371_008.8


def idw_interpolate(
    sample_x: np.ndarray,
    sample_y: np.ndarray,
    values: np.ndarray,
    target_x: np.ndarray,
    target_y: np.ndarray,
    power: float = 2.0,
) -> np.ndarray:
    """Apply z=sum(z_i/d_i^p)/sum(1/d_i^p), preserving exact samples."""
    arrays = tuple(
        np.asarray(value, dtype=float)
        for value in (sample_x, sample_y, values, target_x, target_y)
    )
    sample_x, sample_y, values, target_x, target_y = arrays
    if sample_x.ndim != 1 or sample_y.ndim != 1 or values.ndim != 1:
        raise ValueError("sample_x, sample_y, and values must be one-dimensional")
    if not (sample_x.size == sample_y.size == values.size) or sample_x.size == 0:
        raise ValueError("sample coordinate/value arrays must have the same non-zero length")
    if target_x.shape != target_y.shape:
        raise ValueError("target_x and target_y must have matching shapes")
    if power <= 0 or not np.isfinite(power):
        raise ValueError("IDW power must be a finite number greater than zero")
    if not all(np.all(np.isfinite(array)) for array in arrays):
        raise ValueError("IDW coordinates and values must be finite")

    distance = np.hypot(
        target_x[..., np.newaxis] - sample_x,
        target_y[..., np.newaxis] - sample_y,
    )
    exact = distance <= 1e-9
    safe_distance = np.where(exact, 1.0, distance)
    weights = 1.0 / np.power(safe_distance, power)
    result = np.sum(weights * values, axis=-1) / np.sum(weights, axis=-1)

    exact_cells = np.any(exact, axis=-1)
    if np.any(exact_cells):
        exact_index = np.argmax(exact, axis=-1)
        result[exact_cells] = values[exact_index[exact_cells]]
    return result


def _project_to_local_meters(
    latitude: np.ndarray,
    longitude: np.ndarray,
    reference_latitude: float,
    reference_longitude: float,
) -> tuple[np.ndarray, np.ndarray]:
    """Small-area equirectangular projection used only for IDW distances."""
    x = (
        np.radians(longitude - reference_longitude)
        * EARTH_RADIUS_METERS
        * np.cos(np.radians(reference_latitude))
    )
    y = np.radians(latitude - reference_latitude) * EARTH_RADIUS_METERS
    return x, y


def build_spatial_grid(
    readings: Sequence[Mapping[str, object]],
    rows: int = 24,
    columns: int = 24,
    power: float = 2.0,
) -> dict[str, object]:
    """Aggregate wells and generate Unity-friendly row-major depth/TDS grids."""
    if rows < 2 or columns < 2:
        raise ValueError("rows and columns must both be at least 2")
    if power <= 0 or not np.isfinite(power):
        raise ValueError("IDW power must be a finite number greater than zero")

    latest = aggregate_latest_readings(readings)
    points = merge_duplicate_locations(latest)
    latitude = np.asarray([point["location"]["lat"] for point in points], dtype=float)
    longitude = np.asarray([point["location"]["lng"] for point in points], dtype=float)
    depth = np.asarray([point["depth_to_water_cm"] for point in points], dtype=float)
    tds = np.asarray([point["tds_ppm"] for point in points], dtype=float)

    lat_min, lat_max = float(latitude.min()), float(latitude.max())
    lng_min, lng_max = float(longitude.min()), float(longitude.max())
    if lat_min == lat_max:
        lat_min, lat_max = lat_min - 0.0001, lat_max + 0.0001
    if lng_min == lng_max:
        lng_min, lng_max = lng_min - 0.0001, lng_max + 0.0001

    latitude_axis = np.linspace(lat_min, lat_max, rows)
    longitude_axis = np.linspace(lng_min, lng_max, columns)
    grid_lng, grid_lat = np.meshgrid(longitude_axis, latitude_axis)
    reference_latitude = (lat_min + lat_max) / 2.0
    reference_longitude = (lng_min + lng_max) / 2.0
    sample_x, sample_y = _project_to_local_meters(
        latitude, longitude, reference_latitude, reference_longitude
    )
    grid_x, grid_y = _project_to_local_meters(
        grid_lat, grid_lng, reference_latitude, reference_longitude
    )

    depth_grid = idw_interpolate(sample_x, sample_y, depth, grid_x, grid_y, power)
    tds_grid = idw_interpolate(sample_x, sample_y, tds, grid_x, grid_y, power)

    return {
        "ready": True,
        "schema_version": "1.0",
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "method": {
            "name": "inverse_distance_weighting",
            "power": float(power),
            "distance_unit": "meter",
            "distance_projection": "local_equirectangular",
            "duplicate_coordinate_policy": "mean_before_interpolation",
        },
        "source": {
            "reading_count": len(readings),
            "latest_well_count": len(latest),
            "spatial_point_count": len(points),
            "wells": latest,
        },
        "surface_reference": {
            "metric": "depth_to_water_below_local_wellhead",
            "unit": "cm",
            "is_groundwater_elevation": False,
            "note": (
                "Values are sensor depth below each local wellhead, not elevation "
                "above a shared vertical datum. Add surveyed wellhead elevations "
                "before interpreting this as groundwater elevation."
            ),
        },
        "grid": {
            "rows": rows,
            "columns": columns,
            "layout": "row_major_south_to_north",
            "latitude_axis": np.round(latitude_axis, 8).tolist(),
            "longitude_axis": np.round(longitude_axis, 8).tolist(),
            "depth_to_water_cm": np.round(depth_grid, 3).ravel().tolist(),
            "tds_ppm": np.round(tds_grid, 3).ravel().tolist(),
        },
    }


def build_empty_spatial_grid(
    rows: int = 48,
    columns: int = 48,
    power: float = 2.0,
) -> dict[str, object]:
    """Return a stable API shape while the backend waits for first telemetry."""
    return {
        "ready": False,
        "schema_version": "1.0",
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "message": "Waiting for at least one valid well reading.",
        "method": {
            "name": "inverse_distance_weighting",
            "power": float(power),
            "distance_unit": "meter",
            "distance_projection": "local_equirectangular",
            "duplicate_coordinate_policy": "mean_before_interpolation",
        },
        "source": {
            "reading_count": 0,
            "latest_well_count": 0,
            "spatial_point_count": 0,
            "wells": [],
        },
        "surface_reference": {
            "metric": "depth_to_water_below_local_wellhead",
            "unit": "cm",
            "is_groundwater_elevation": False,
            "note": (
                "Values are sensor depth below each local wellhead, not elevation "
                "above a shared vertical datum."
            ),
        },
        "grid": {
            "rows": rows,
            "columns": columns,
            "layout": "row_major_south_to_north",
            "latitude_axis": [],
            "longitude_axis": [],
            "depth_to_water_cm": [],
            "tds_ppm": [],
        },
    }


def build_terrain_matrix(
    wells: Sequence[Mapping[str, object]],
    rows: int = 24,
    columns: int = 24,
    power: float = 2.0,
) -> dict[str, object]:
    """Backward-compatible alias for API code; output remains depth-labelled."""
    return build_spatial_grid(wells, rows, columns, power)
