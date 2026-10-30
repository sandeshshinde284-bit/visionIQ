from fastapi.testclient import TestClient

from app.config import Settings
from app.main import create_app


def test_health_returns_expected_payload(client: TestClient) -> None:
    response = client.get("/health")

    assert response.status_code == 200
    assert response.json() == {"status": "healthy", "service": "VisionIQ.Intelligence"}


def test_health_uses_configured_service_name() -> None:
    settings = Settings(
        _env_file=None,
        service_name="Custom.Name",
        db_intelligence_user="visioniq_intelligence",
        db_intelligence_password="test-only-intelligence-password",
    )

    with TestClient(create_app(settings)) as client:
        assert client.get("/health").json()["service"] == "Custom.Name"


def test_startup_does_not_need_external_credentials(monkeypatch, test_settings: Settings) -> None:
    for name in ("GOOGLE_APPLICATION_CREDENTIALS", "ANTHROPIC_API_KEY", "GCP_PROJECT_ID"):
        monkeypatch.delenv(name, raising=False)

    with TestClient(create_app(test_settings)) as client:
        assert client.get("/health").status_code == 200
