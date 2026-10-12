from mock_publisher import mock_payload


def test_mock_payload_matches_firmware_schema_and_is_labelled() -> None:
    payload = mock_payload(0)
    assert set(("device", "distance_cm", "tds_ppm", "ph")) <= payload.keys()
    assert payload["device"].startswith("MOCK-")
    assert payload["mock"] is True

