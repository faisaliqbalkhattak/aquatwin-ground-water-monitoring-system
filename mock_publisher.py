"""Explicit, isolated mock publisher for AquaTwin HiveMQ testing."""

from __future__ import annotations

import argparse
import json
from threading import Event
import time

import paho.mqtt.client as mqtt

from cloud_monitor.config import (
    ConfigurationError,
    MqttSettings,
    create_tls_context,
    unique_client_id,
)


MOCK_WELLS = (
    ("WELL_MOCK_01", 24.453900, 54.377300, 143.14),
    ("WELL_MOCK_02", 24.458200, 54.381100, 166.20),
    ("WELL_MOCK_03", 24.449500, 54.371800, 121.75),
    ("WELL_MOCK_04", 24.462400, 54.369900, 188.40),
    ("WELL_MOCK_05", 24.446100, 54.386000, 154.60),
    ("WELL_MOCK_06", 24.470100, 54.390200, 201.30),
    ("WELL_MOCK_07", 24.440800, 54.362500, 134.90),
    ("WELL_MOCK_08", 24.474500, 54.375700, 176.80),
)


def mock_payload(sequence: int, well_index: int = 0) -> dict[str, object]:
    """Generate one exact-schema packet for a deterministic mock well."""
    well_id, latitude, longitude, baseline_depth = MOCK_WELLS[well_index]
    depth = round(baseline_depth + (sequence % 6) * 0.25, 2)
    return {
        "well_id": well_id,
        "location": {"lat": latitude, "lng": longitude},
        "depth_to_water_cm": depth,
        "water_column_height_cm": round(500.0 - depth, 2),
        "tds_ppm": round(320.3 + (sequence % 5) * 0.5, 1),
        "status": "ONLINE",
        "timestamp": int(time.time()),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Explicit AquaTwin MOCK MQTT publisher")
    parser.add_argument("--env-file", default=".env")
    parser.add_argument(
        "--count",
        type=int,
        default=1,
        help="publish cycles; 0 runs continuously until Ctrl+C (default: 1)",
    )
    parser.add_argument("--wells", type=int, default=5, help="mock wells, 1-8 (default: 5)")
    parser.add_argument("--interval", type=float, default=3.0, help="seconds between cycles")
    args = parser.parse_args()
    if args.count < 0 or args.interval < 0 or not 1 <= args.wells <= len(MOCK_WELLS):
        parser.error("--count must be >= 0, --wells 1-8, and --interval >= 0")

    try:
        settings = MqttSettings.from_env(args.env_file)
    except ConfigurationError as exc:
        print(f"Configuration error: {exc}")
        return 2

    connected = Event()
    failed: list[str] = []
    client = mqtt.Client(
        callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
        client_id=unique_client_id("aquatwin-mock"),
        protocol=mqtt.MQTTv311,
    )
    client.username_pw_set(settings.username, settings.password)
    client.tls_set_context(create_tls_context())

    def on_connect(client, userdata, flags, reason_code, properties) -> None:
        if reason_code == 0:
            connected.set()
        else:
            failed.append(str(reason_code))
            connected.set()

    client.on_connect = on_connect
    try:
        print(f"[MOCK] Connecting securely to {settings.host}:{settings.port}...")
        client.connect(settings.host, settings.port, keepalive=30)
        client.loop_start()
        if not connected.wait(timeout=15):
            print("[MOCK] Connection timed out; no data was published.")
            return 1
        if failed:
            print(f"[MOCK] Broker rejected connection: {failed[0]}; no data was published.")
            return 1

        print(f"[MOCK] Connected. Publishing {args.wells} wells with frozen topic contract.")
        sequence = 0
        while args.count == 0 or sequence < args.count:
            for well_index in range(args.wells):
                packet = mock_payload(sequence, well_index)
                topic = f"aquatwin/well/{packet['well_id']}/telemetry"
                payload = json.dumps(packet, separators=(",", ":"))
                info = client.publish(topic, payload, qos=0, retain=False)
                info.wait_for_publish(timeout=10)
                if not info.is_published():
                    print(f"[MOCK] Publish confirmation timed out for {packet['well_id']}.")
                    return 1
                print(f"[MOCK] {topic}: {payload}")
            sequence += 1
            if args.count == 0 or sequence < args.count:
                time.sleep(args.interval)
        return 0
    except (OSError, mqtt.MQTTException) as exc:
        print(f"[MOCK] MQTT error: {exc}")
        return 1
    except KeyboardInterrupt:
        print("\n[MOCK] Stopped by user.")
        return 130
    finally:
        try:
            client.disconnect()
            client.loop_stop()
        except RuntimeError:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
