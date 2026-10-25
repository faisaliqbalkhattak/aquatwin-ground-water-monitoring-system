"""Run with: python -m cloud_monitor"""

from __future__ import annotations

import argparse
import sys

from .client import AquaTwinMonitor
from .config import ConfigurationError, MqttSettings


def main() -> int:
    parser = argparse.ArgumentParser(description="Secure AquaTwin HiveMQ telemetry monitor")
    parser.add_argument("--env-file", default=".env", help="dotenv file (default: .env)")
    parser.add_argument("--topic", help="override MQTT_TOPIC, useful for the mock topic")
    args = parser.parse_args()
    try:
        settings = MqttSettings.from_env(args.env_file, args.topic)
    except ConfigurationError as exc:
        print(f"Configuration error: {exc}", file=sys.stderr)
        return 2
    AquaTwinMonitor(settings).run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

