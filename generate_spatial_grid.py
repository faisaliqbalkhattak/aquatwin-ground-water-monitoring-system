"""Generate Unity-ready AquaTwin IDW JSON and an optional local SVG preview."""

from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import tempfile
from typing import Sequence

os.environ.setdefault(
    "MPLCONFIGDIR", str(Path(tempfile.gettempdir()) / "aquatwin-matplotlib")
)
import matplotlib
matplotlib.use("Agg")
from matplotlib import colormaps
from matplotlib.colors import Normalize
from matplotlib.cm import ScalarMappable
import matplotlib.pyplot as plt
import numpy as np

from aquatwin_backend.aggregation import SpatialInputError
from aquatwin_backend.idw import EARTH_RADIUS_METERS, build_spatial_grid


def write_surface_preview(grid_document: dict[str, object], output_path: Path) -> None:
    """Render a publication-style 3D depth surface and 2D contour map."""
    grid = grid_document["grid"]
    rows, columns = int(grid["rows"]), int(grid["columns"])
    latitude = np.asarray(grid["latitude_axis"], dtype=float)
    longitude = np.asarray(grid["longitude_axis"], dtype=float)
    depth = np.asarray(grid["depth_to_water_cm"], dtype=float).reshape(rows, columns)
    tds = np.asarray(grid["tds_ppm"], dtype=float).reshape(rows, columns)
    lat_grid, lng_grid = np.meshgrid(latitude, longitude, indexing="ij")
    reference_latitude = float(latitude.mean())
    reference_longitude = float(longitude.mean())
    x_grid = (
        np.radians(lng_grid - reference_longitude)
        * EARTH_RADIUS_METERS
        * math.cos(math.radians(reference_latitude))
    )
    y_grid = np.radians(lat_grid - reference_latitude) * EARTH_RADIUS_METERS
    z_grid = -depth / 100.0

    wells = grid_document["source"]["wells"]
    well_lat = np.asarray([well["location"]["lat"] for well in wells], dtype=float)
    well_lng = np.asarray([well["location"]["lng"] for well in wells], dtype=float)
    well_x = (
        np.radians(well_lng - reference_longitude)
        * EARTH_RADIUS_METERS
        * math.cos(math.radians(reference_latitude))
    )
    well_y = np.radians(well_lat - reference_latitude) * EARTH_RADIUS_METERS
    well_z = -np.asarray([well["depth_to_water_cm"] for well in wells], dtype=float) / 100.0

    background = "#07111f"
    panel = "#0d1b2a"
    foreground = "#e8f1f8"
    muted = "#93a9ba"
    contour_colour = "#fff176"
    cmap = colormaps["turbo"]
    normalization = Normalize(vmin=float(tds.min()), vmax=float(tds.max()))

    figure = plt.figure(figsize=(16, 9), facecolor=background)
    layout = figure.add_gridspec(
        1,
        2,
        width_ratios=(1.65, 1.0),
        left=0.035,
        right=0.985,
        bottom=0.17,
        top=0.84,
        wspace=0.18,
    )
    surface_axes = figure.add_subplot(layout[0, 0], projection="3d")
    map_axes = figure.add_subplot(layout[0, 1])
    surface_axes.set_facecolor(panel)
    map_axes.set_facecolor(panel)

    surface_axes.plot_surface(
        x_grid,
        y_grid,
        z_grid,
        facecolors=cmap(normalization(tds)),
        rstride=1,
        cstride=1,
        linewidth=0.12,
        edgecolor="#23384b",
        antialiased=True,
        shade=True,
        alpha=0.96,
    )
    depth_levels_m = np.linspace(float(z_grid.min()), float(z_grid.max()), 8)
    surface_axes.contour(
        x_grid,
        y_grid,
        z_grid,
        levels=depth_levels_m,
        colors=contour_colour,
        linewidths=0.85,
        alpha=0.9,
    )
    surface_axes.scatter(
        well_x,
        well_y,
        well_z,
        s=70,
        c="#ffffff",
        edgecolors="#06101d",
        linewidths=1.2,
        depthshade=False,
        label="Simulated wells",
    )
    for well, x, y, z in zip(wells, well_x, well_y, well_z):
        surface_axes.text(x, y, z + 0.035, str(well["well_id"]).replace("WELL_SIM_", "W"),
                          color=foreground, fontsize=8, weight="bold")
    surface_axes.view_init(elev=34, azim=-128)
    surface_axes.set_title("3D IDW depth surface", color=foreground, fontsize=17, pad=18)
    surface_axes.set_xlabel("East-West offset (m)", color=muted, labelpad=10)
    surface_axes.set_ylabel("North-South offset (m)", color=muted, labelpad=10)
    surface_axes.set_zlabel("Relative vertical value: -depth (m)", color=muted, labelpad=10)
    surface_axes.tick_params(colors=muted, labelsize=8)
    surface_axes.legend(loc="upper left", frameon=False, labelcolor=foreground)
    for axis in (surface_axes.xaxis, surface_axes.yaxis, surface_axes.zaxis):
        axis.pane.set_facecolor((0.05, 0.11, 0.17, 0.94))
        axis.pane.set_edgecolor("#365066")
        axis._axinfo["grid"]["color"] = (0.25, 0.38, 0.48, 0.35)

    filled = map_axes.contourf(x_grid, y_grid, tds, levels=18, cmap=cmap, norm=normalization)
    depth_lines = map_axes.contour(
        x_grid,
        y_grid,
        depth,
        levels=8,
        colors=contour_colour,
        linewidths=1.0,
    )
    map_axes.clabel(depth_lines, inline=True, fontsize=8, fmt="%.0f cm", colors=contour_colour)
    map_axes.scatter(
        well_x,
        well_y,
        s=65,
        c="#ffffff",
        edgecolors="#07111f",
        linewidths=1.2,
        zorder=5,
    )
    for well, x, y in zip(wells, well_x, well_y):
        horizontal_offset = 8 if x <= 0 else -8
        vertical_offset = 8 if y <= 0 else -12
        map_axes.annotate(
            str(well["well_id"]).replace("WELL_SIM_", "W"),
            (x, y),
            xytext=(horizontal_offset, vertical_offset),
            textcoords="offset points",
            color=foreground,
            fontsize=8,
            weight="bold",
            ha="left" if horizontal_offset > 0 else "right",
        )
    map_axes.set_title("TDS concentration with depth contours", color=foreground, fontsize=17, pad=18)
    map_axes.set_xlabel("East-West offset (m)", color=muted)
    map_axes.set_ylabel("North-South offset (m)", color=muted)
    map_axes.tick_params(colors=muted, labelsize=8)
    map_axes.set_aspect("equal", adjustable="box")
    map_axes.grid(color="#405a70", alpha=0.2, linewidth=0.6)
    for spine in map_axes.spines.values():
        spine.set_color("#365066")

    colour_bar_axes = figure.add_axes((0.19, 0.085, 0.62, 0.018))
    colour_bar = figure.colorbar(
        ScalarMappable(norm=normalization, cmap=cmap),
        cax=colour_bar_axes,
        orientation="horizontal",
    )
    colour_bar.set_label("Interpolated TDS (ppm)", color=foreground, fontsize=11)
    colour_bar.ax.tick_params(colors=muted, labelsize=8)
    colour_bar.outline.set_edgecolor("#365066")

    figure.suptitle(
        "AquaTwin | Multi-Well Groundwater IDW Analysis",
        color=foreground,
        fontsize=23,
        weight="bold",
        y=0.975,
    )
    figure.text(
        0.5,
        0.925,
        f"SIMULATED TEST DATA  |  {len(wells)} wells  |  {rows}x{columns} grid  |  IDW p={grid_document['method']['power']:.1f}",
        ha="center",
        color="#6fd3ff",
        fontsize=11,
        weight="bold",
    )
    figure.text(
        0.5,
        0.025,
        "Scientific caution: depth is measured below each local wellhead. This is not surveyed groundwater elevation or hydraulic head.",
        ha="center",
        color="#ffcc80",
        fontsize=10,
    )

    output_path.parent.mkdir(parents=True, exist_ok=True)
    figure.savefig(output_path, dpi=180, facecolor=figure.get_facecolor())
    plt.close(figure)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Generate AquaTwin IDW spatial grids")
    parser.add_argument("--input", default="examples/simulated_wells.json")
    parser.add_argument("--output", default="output/spatial_grid.json")
    parser.add_argument("--preview", help="optional SVG surface preview path")
    parser.add_argument("--rows", type=int, default=24)
    parser.add_argument("--columns", type=int, default=24)
    parser.add_argument("--power", type=float, default=2.0)
    args = parser.parse_args(argv)

    try:
        source = json.loads(Path(args.input).read_text(encoding="utf-8"))
        readings = source["readings"] if isinstance(source, dict) else source
        document = build_spatial_grid(readings, args.rows, args.columns, args.power)
    except (OSError, json.JSONDecodeError, KeyError, TypeError, ValueError, SpatialInputError) as exc:
        parser.error(str(exc))

    if isinstance(source, dict):
        document["data_source"] = source.get("data_source", "unspecified")
        document["source_note"] = source.get("note", "")
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(document, indent=2), encoding="utf-8")
    if args.preview:
        write_surface_preview(document, Path(args.preview))

    grid = document["grid"]
    print(
        f"Generated {grid['rows']}x{grid['columns']} grid from "
        f"{document['source']['latest_well_count']} wells: {output}"
    )
    if args.preview:
        print(f"Preview: {args.preview}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
