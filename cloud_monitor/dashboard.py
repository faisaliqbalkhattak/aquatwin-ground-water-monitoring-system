"""Dependency-free terminal dashboard."""

from __future__ import annotations

import sys
import time

from .state import MonitorSnapshot, MonitorState


def render_dashboard(snapshot: MonitorSnapshot, topic: str) -> str:
    telemetry = snapshot.telemetry
    if telemetry is None:
        device = distance = tds = ph = received = "waiting for first valid message"
        age = "n/a"
    else:
        device = telemetry.device
        distance = f"{telemetry.distance_cm:.2f} cm"
        tds = f"{telemetry.tds_ppm:.2f} ppm"
        ph = "not present" if telemetry.ph is None else f"{telemetry.ph:.2f}"
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
            f"Device     : {device}",
            f"Distance   : {distance}",
            f"TDS        : {tds}",
            f"pH         : {ph}",
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

