"""Dependency-free terminal dashboard."""

from __future__ import annotations

from datetime import datetime, timezone
import sys
from .state import MonitorSnapshot, MonitorState


def render_dashboard(snapshot: MonitorSnapshot, topic: str) -> str:
    telemetry = snapshot.telemetry
    if telemetry is None:
        waiting = "waiting for first valid message"
        well_id = location = depth = water_column = tds = status = source_time = received = waiting
        age = "n/a"
    else:
        well_id = telemetry.well_id
        location = f"{telemetry.latitude:.6f}, {telemetry.longitude:.6f}"
        depth = f"{telemetry.depth_to_water_cm:.2f} cm"
        water_column = f"{telemetry.water_column_height_cm:.2f} cm"
        tds = f"{telemetry.tds_ppm:.2f} ppm"
        status = telemetry.status
        source_time = datetime.fromtimestamp(
            telemetry.source_timestamp, timezone.utc
        ).astimezone().isoformat(timespec="seconds")
        received = telemetry.received_at.astimezone().isoformat(timespec="seconds")
        age = f"{snapshot.age_seconds:.1f} s" if snapshot.age_seconds is not None else "n/a"
    warning = "STALE/OFFLINE: no recent valid telemetry" if snapshot.stale else "LIVE TELEMETRY"
    error = snapshot.last_error or "none"
    return "\n".join(
        (
            "AquaTwin MQTT Monitor",
            "=" * 48,
            f"Connection : {snapshot.connection}",
            f"Telemetry  : {warning}",
            f"Topic      : {topic}",
            f"Well ID    : {well_id}",
            f"Location   : {location}",
            f"Depth      : {depth}",
            f"Water col. : {water_column}",
            f"TDS        : {tds}",
            f"Status     : {status}",
            f"Source time: {source_time}",
            f"Received   : {received}",
            f"Messages   : {snapshot.message_count} valid / {snapshot.invalid_count} invalid",
            f"Last age   : {age}",
            f"Last error : {error}",
            "",
            "Ctrl+C se monitor band karein.",
        )
    )


def run_dashboard(state: MonitorState, topic: str, stop_event) -> None:
    interactive = sys.stdout.isatty()
    last_render = ""
    while not stop_event.is_set():
        rendered = render_dashboard(state.snapshot(), topic)
        if interactive:
            print("\033[2J\033[H" + rendered, end="", flush=True)
        elif rendered != last_render:
            print(rendered, flush=True)
        last_render = rendered
        stop_event.wait(1.0)
