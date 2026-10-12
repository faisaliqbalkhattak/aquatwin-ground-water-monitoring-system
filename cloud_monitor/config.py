"""Environment-only configuration and TLS helpers."""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
import os
import ssl
import uuid

from dotenv import load_dotenv


DEFAULT_TOPIC = "digitaltwin/well01/sensors"


class ConfigurationError(ValueError):
    """Raised when required MQTT configuration is missing or unsafe."""


def validate_topic(topic: str) -> str:
    """Validate the concrete topic used by the firmware (not a wildcard filter)."""
    if not isinstance(topic, str) or not topic.strip():
        raise ConfigurationError("MQTT topic cannot be empty.")
    topic = topic.strip()
    if "\x00" in topic or "+" in topic or "#" in topic:
        raise ConfigurationError("MQTT topic must be concrete and cannot contain NUL, +, or #.")
    if len(topic.encode("utf-8")) > 65_535:
        raise ConfigurationError("MQTT topic is too long.")
    return topic


def create_tls_context() -> ssl.SSLContext:
    """Use OS trusted CAs, require a valid certificate, and verify the hostname."""
    context = ssl.create_default_context(purpose=ssl.Purpose.SERVER_AUTH)
    context.verify_mode = ssl.CERT_REQUIRED
    context.check_hostname = True
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    return context


def unique_client_id(prefix: str) -> str:
    return f"{prefix}-{uuid.uuid4().hex[:12]}"


@dataclass(frozen=True)
class MqttSettings:
    host: str
    port: int
    username: str
    password: str = field(repr=False)
    topic: str = DEFAULT_TOPIC
    stale_timeout: float = 10.0

    @classmethod
    def from_env(
        cls,
        env_file: str | Path = ".env",
        topic_override: str | None = None,
    ) -> "MqttSettings":
        load_dotenv(dotenv_path=env_file, override=False)

        host = os.getenv("MQTT_HOST", "").strip()
        username = os.getenv("MQTT_USERNAME", "").strip()
        password = os.getenv("MQTT_PASSWORD", "")
        missing = [
            name
            for name, value in (
                ("MQTT_HOST", host),
                ("MQTT_USERNAME", username),
                ("MQTT_PASSWORD", password),
            )
            if not value
        ]
        if missing:
            raise ConfigurationError(
                "Missing required setting(s): " + ", ".join(missing) +
                ". Copy .env.example to .env and fill in your private credentials."
            )

        try:
            port = int(os.getenv("MQTT_PORT", "8883"))
            stale_timeout = float(os.getenv("MQTT_STALE_TIMEOUT", "10"))
        except ValueError as exc:
            raise ConfigurationError("MQTT_PORT and MQTT_STALE_TIMEOUT must be numbers.") from exc
        if not 1 <= port <= 65_535:
            raise ConfigurationError("MQTT_PORT must be between 1 and 65535.")
        if stale_timeout <= 0:
            raise ConfigurationError("MQTT_STALE_TIMEOUT must be greater than zero.")

        topic = validate_topic(topic_override or os.getenv("MQTT_TOPIC", DEFAULT_TOPIC))
        return cls(host, port, username, password, topic, stale_timeout)

