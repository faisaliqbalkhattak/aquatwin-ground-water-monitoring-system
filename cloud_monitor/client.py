"""Paho MQTT Callback API v2 subscriber."""

from __future__ import annotations

from threading import Event

import paho.mqtt.client as mqtt

from .config import MqttSettings, create_tls_context, unique_client_id
from .dashboard import run_dashboard
from .state import MonitorState
from .telemetry import TelemetryError, parse_telemetry


class AquaTwinMonitor:
    def __init__(self, settings: MqttSettings) -> None:
        self.settings = settings
        self.state = MonitorState(settings.stale_timeout)
        self.stop_event = Event()
        self.client = mqtt.Client(
            callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
            client_id=unique_client_id("aquatwin-monitor"),
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
            self.state.set_connection(f"AUTH/CONNECT FAILED ({reason_code})", False)
            return
        self.state.set_connection("CONNECTED / SUBSCRIBING", True)
        result, _mid = client.subscribe(self.settings.topic, qos=0)
        if result == mqtt.MQTT_ERR_SUCCESS:
            self.state.set_connection("CONNECTED / WAITING FOR TELEMETRY", True)
        else:
            self.state.set_connection(f"CONNECTED / SUBSCRIBE FAILED ({result})", True)

    def _on_connect_fail(self, client, userdata) -> None:
        self.state.set_connection("CONNECTION FAILED / RETRYING", False)

    def _on_disconnect(self, client, userdata, disconnect_flags, reason_code, properties) -> None:
        if self.stop_event.is_set() or reason_code == 0:
            self.state.set_connection("DISCONNECTED", False)
        else:
            self.state.set_connection(f"DISCONNECTED ({reason_code}) / RETRYING", False)

    def _on_message(self, client, userdata, message) -> None:
        try:
            telemetry = parse_telemetry(
                message.payload,
                message.topic,
                self.settings.topic,
            )
        except TelemetryError as exc:
            self.state.record_invalid(str(exc))
            return
        self.state.record(telemetry)
        self.state.set_connection("CONNECTED / TELEMETRY RECEIVED", True)

    def run(self) -> None:
        self.state.set_connection("CONNECTING", False)
        self.client.connect_async(
            self.settings.host,
            self.settings.port,
            keepalive=30,
        )
        self.client.loop_start()
        try:
            run_dashboard(self.state, self.settings.topic, self.stop_event)
        except KeyboardInterrupt:
            pass
        finally:
            self.stop()

    def stop(self) -> None:
        self.stop_event.set()
        self.client.disconnect()
        self.client.loop_stop()

