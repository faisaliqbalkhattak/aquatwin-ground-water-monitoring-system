"""Explicit, isolated mock publisher for AquaTwin HiveMQ testing."""

from __future__ import annotations

import argparse
import json
import os
from threading import Event
import time

import paho.mqtt.client as mqtt
from dotenv import load_dotenv

from cloud_monitor.config import (
    ConfigurationError,
    MqttSettings,
    create_tls_context,
    unique_client_id,
    validate_topic,
)


def mock_payload(sequence: int) -> dict[str, object]:
    """Match the firmware fields while making simulation unmistakable."""
    return {
        "device": "MOCK-well01",
        "distance_cm": round(10.0 + (sequence % 6) * 0.25, 2),
        "tds_ppm": round(300.0 + (sequence % 5) * 2.5, 2),
        "ph": round(7.0 + (sequence % 3) * 0.05, 2),
        "mock": True,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Explicit AquaTwin MOCK MQTT publisher")
    parser.add_argument("--env-file", default=".env")
    parser.add_argument("--topic", help="test topic; defaults to MQTT_TEST_TOPIC")
    parser.add_argument("--count", type=int, default=1, help="messages to send (default: 1)")
    parser.add_argument("--interval", type=float, default=2.0, help="seconds between messages")
    parser.add_argument(
        "--allow-live-topic",
        action="store_true",
        help="required if intentionally publishing MOCK data to the physical-device topic",
    )
    args = parser.parse_args()
    if args.count < 1 or args.interval < 0:
        parser.error("--count must be at least 1 and --interval cannot be negative")

    load_dotenv(args.env_file, override=False)
    try:
        settings = MqttSettings.from_env(args.env_file)
        test_topic = validate_topic(
            args.topic or os.getenv("MQTT_TEST_TOPIC", settings.topic + "/mock")
        )
    except ConfigurationError as exc:
        print(f"Configuration error: {exc}")
        return 2
    if test_topic == settings.topic and not args.allow_live_topic:
        print(
            "Safety stop: mock topic equals the live firmware topic. "
            "Use MQTT_TEST_TOPIC or explicitly pass --allow-live-topic."
        )
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

        print(f"[MOCK] Connected. Test topic: {test_topic}")
        for sequence in range(args.count):
            payload = json.dumps(mock_payload(sequence), separators=(",", ":"))
            info = client.publish(test_topic, payload, qos=0, retain=False)
            info.wait_for_publish(timeout=10)
            if not info.is_published():
                print("[MOCK] Publish confirmation timed out.")
                return 1
            print(f"[MOCK] Published {sequence + 1}/{args.count}: {payload}")
            if sequence + 1 < args.count:
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

