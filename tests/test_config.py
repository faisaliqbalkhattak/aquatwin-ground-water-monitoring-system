import ssl

import pytest

from cloud_monitor.config import ConfigurationError, create_tls_context, unique_client_id, validate_topic
from cloud_monitor.config import MqttSettings
from cloud_monitor.client import AquaTwinMonitor


def test_actual_topic_is_valid() -> None:
    assert validate_topic("digitaltwin/well01/sensors") == "digitaltwin/well01/sensors"


@pytest.mark.parametrize("topic", ["", "sensors/+", "sensors/#", "bad\x00topic"])
def test_invalid_topics_are_rejected(topic: str) -> None:
    with pytest.raises(ConfigurationError):
        validate_topic(topic)


def test_tls_requires_certificate_and_hostname_verification() -> None:
    context = create_tls_context()
    assert context.verify_mode == ssl.CERT_REQUIRED
    assert context.check_hostname is True
    assert context.minimum_version == ssl.TLSVersion.TLSv1_2


def test_client_ids_are_unique() -> None:
    assert unique_client_id("aquatwin") != unique_client_id("aquatwin")


def test_monitor_uses_paho_callback_api_v2_without_connecting() -> None:
    settings = MqttSettings("example.invalid", 8883, "user", "secret")
    monitor = AquaTwinMonitor(settings)
    assert monitor.client.callback_api_version.name == "VERSION2"
    assert "secret" not in repr(settings)
