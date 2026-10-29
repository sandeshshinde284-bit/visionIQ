"""Offline validation for the guarded, private Cloud SQL migration configuration."""
import pytest

from db.cloud_manage import CloudConfig


def make_config(**values):
    data = {
        "_env_file": None,
        "db_target": "cloud",
        "gcp_project_id": "test-project",
        "db_cloud_instance": "test-project:europe-west3:visioniqdev",
        "db_host": "10.102.16.8",
        "db_port": 5432,
        "db_cloud_name": "visioniq_dev",
        "db_cloud_user": "postgres",
        "db_cloud_password": "cloud-admin-secret",
        "db_api_user": "visioniq_api",
        "db_cloud_api_password": "cloud-api-secret",
        "db_intelligence_user": "visioniq_intelligence",
        "db_cloud_intelligence_password": "cloud-intelligence-secret",
    }
    data.update(values)
    return CloudConfig(**data)


def test_approved_cloud_configuration():
    settings = make_config()
    settings.check()
    assert settings.url().query["sslmode"] == "require"
    assert settings.url().database == "visioniq_dev"
    assert "cloud-admin-secret" not in str(settings.url())


def test_existing_mixed_case_cloud_admin_username():
    settings = make_config(db_cloud_user="visionIQ")
    settings.check()
    assert settings.url().username == "visionIQ"


@pytest.mark.parametrize("changed", [
    {"db_target": "local"},
    {"db_host": "127.0.0.1"},
    {"db_host": "8.8.8.8"},
    {"gcp_project_id": "wrong-project"},
    {"db_cloud_name": "postgres"},
    {"db_cloud_api_password": "cloud-admin-secret"},
])
def test_rejects_unsafe_cloud_configuration(changed):
    with pytest.raises((ValueError, Exception)):
        make_config(**changed).check()
