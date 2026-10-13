from types import SimpleNamespace

from aquatwin_backend.ingestion import MqttIngestionService
from aquatwin_backend.storage import TelemetryStore
from cloud_monitor.config import MqttSettings


VALID_PAYLOAD = b'''{
  "well_id":"WELL_TEST_01",
  "location":{"lat":24.45,"lng":54.37},
  "depth_to_water_cm":140.0,
  "water_column_height_cm":360.0,
  "tds_ppm":320.0,
  "status":"ONLINE",
  "timestamp":1791700194
}'''


def test_valid_mqtt_message_is_stored_and_notifies_update(tmp_path) -> None:
    store = TelemetryStore(tmp_path / "telemetry.db")
    notifications = []
    service = MqttIngestionService(
        MqttSettings("example.invalid", 8883, "user", "secret"),
        store,
        on_stored=lambda: notifications.append(True),
    )
    message = SimpleNamespace(
        topic="aquatwin/well/WELL_TEST_01/telemetry",
        payload=VALID_PAYLOAD,
    )
    service._on_message(None, None, message)

    assert store.latest_wells()[0]["well_id"] == "WELL_TEST_01"
    assert notifications == [True]
    assert service.snapshot()["valid_messages"] == 1


def test_invalid_mqtt_message_is_rejected_without_notification(tmp_path) -> None:
    store = TelemetryStore(tmp_path / "telemetry.db")
    notifications = []
    service = MqttIngestionService(
        MqttSettings("example.invalid", 8883, "user", "secret"),
        store,
        on_stored=lambda: notifications.append(True),
    )
    message = SimpleNamespace(
        topic="aquatwin/well/WELL_TEST_01/telemetry",
        payload=b"not-json",
    )
    service._on_message(None, None, message)

    assert store.latest_wells() == []
    assert notifications == []
    assert service.snapshot()["invalid_messages"] == 1
