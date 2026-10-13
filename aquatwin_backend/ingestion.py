"""Secure HiveMQ consumer that stores only schema-valid packets."""

from __future__ import annotations

import logging
from datetime import datetime, timezone
from threading import Lock
from typing import Callable

import paho.mqtt.client as mqtt

from cloud_monitor.config import MqttSettings, create_tls_context, unique_client_id
from cloud_monitor.telemetry import TelemetryError, parse_telemetry

from .storage import TelemetryStore


LOGGER = logging.getLogger(__name__)


class MqttIngestionService:
    def __init__(
        self,
        settings: MqttSettings,
        store: TelemetryStore,
        on_stored: Callable[[], None] | None = None,
    ) -> None:
        self.settings = settings
        self.store = store
        self.on_stored = on_stored
        self._state_lock = Lock()
        self._connected = False
        self._valid_messages = 0
        self._invalid_messages = 0
        self._last_message_at: str | None = None
        self._last_error: str | None = None
        self.client = mqtt.Client(
            callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
            client_id=unique_client_id("aquatwin-backend"),
            protocol=mqtt.MQTTv311,
            reconnect_on_failure=True,
        )
        self.client.username_pw_set(settings.username, settings.password)
        self.client.tls_set_context(create_tls_context())
        self.client.reconnect_delay_set(min_delay=1, max_delay=30)
        self.client.on_connect = self._on_connect
        self.client.on_connect_fail = self._on_connect_fail
        self.client.on_disconnect = self._on_disconnect
        self.client.on_message = self._on_message

    def _on_connect(self, client, userdata, flags, reason_code, properties) -> None:
        if reason_code != 0:
            with self._state_lock:
                self._connected = False
                self._last_error = f"HiveMQ connection rejected: {reason_code}"
            LOGGER.error("HiveMQ connection rejected: %s", reason_code)
            return
        with self._state_lock:
            self._connected = True
            self._last_error = None
        result, _ = client.subscribe(self.settings.topic, qos=0)
        if result == mqtt.MQTT_ERR_SUCCESS:
            LOGGER.info("Subscribed to %s", self.settings.topic)
        else:
            LOGGER.error("Subscription failed with code %s", result)

    def _on_connect_fail(self, client, userdata) -> None:
        with self._state_lock:
            self._connected = False
            self._last_error = "HiveMQ connection failed; retrying"

    def _on_disconnect(self, client, userdata, disconnect_flags, reason_code, properties) -> None:
        with self._state_lock:
            self._connected = False
            if reason_code != 0:
                self._last_error = f"HiveMQ disconnected: {reason_code}"

    def _on_message(self, client, userdata, message) -> None:
        try:
            telemetry = parse_telemetry(
                message.payload,
                message.topic,
                self.settings.topic,
            )
        except TelemetryError as exc:
            with self._state_lock:
                self._invalid_messages += 1
                self._last_error = str(exc)
            LOGGER.warning("Rejected telemetry on %s: %s", message.topic, exc)
            return
        self.store.insert(telemetry)
        with self._state_lock:
            self._valid_messages += 1
            self._last_message_at = datetime.now(timezone.utc).isoformat()
            self._last_error = None
        if self.on_stored is not None:
            self.on_stored()
        LOGGER.info("Stored telemetry for %s", telemetry.well_id)

    def snapshot(self) -> dict[str, object]:
        with self._state_lock:
            return {
                "connected": self._connected,
                "valid_messages": self._valid_messages,
                "invalid_messages": self._invalid_messages,
                "last_message_at": self._last_message_at,
                "last_error": self._last_error,
                "topic": self.settings.topic,
            }

    def start(self) -> None:
        self.client.connect_async(self.settings.host, self.settings.port, keepalive=30)
        self.client.loop_start()

    def stop(self) -> None:
        self.client.disconnect()
        self.client.loop_stop()
