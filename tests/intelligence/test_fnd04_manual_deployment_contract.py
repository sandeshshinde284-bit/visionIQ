"""Static safety contract for FND-04 manual deployment scripts.

These tests intentionally never invoke GCP, Docker, read credentials or
create corporate resources. Live deployment acceptance is a separate gate.
"""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MANUAL = ROOT / "scripts" / "manual-deploy"


def test_manual_build_never_requires_cloud_build_or_mutates_default() -> None:
    """Reviewed source and an explicit -Push gate are required before uploads."""
    script = (MANUAL / "01-build-push.ps1").read_text(encoding="utf-8")
    assert "-ReviewedCommit" in script
    assert "if ($Push)" in script
    assert "status --porcelain" in script
    assert "--platform linux/amd64" in script
    assert "team-visioniq-docker-dev" in script
    assert "gcloud auth configure-docker" in script
    assert "docker push" in script
    assert "PLAN ONLY" in script
    assert "cloudbuild" not in script.lower()


def test_private_deploy_has_explicit_release_and_secret_guards() -> None:
    """No default service deployment, public exposure or raw DB password."""
    script = (MANUAL / "02-deploy-services.ps1").read_text(encoding="utf-8")
    assert "if (-not $Deploy)" in script
    assert "--no-allow-unauthenticated" in script
    assert "--ingress=internal" in script
    assert "private-ranges-only" in script
    assert "--set-secrets=" in script
    assert "[ValidateRange(1, 2147483647)]" in script
    assert "visioniq-runtime@" in script
    assert "team-visioniq-docker-dev" in script
    assert "visioniq-api" in script
    assert "visioniq-intelligence" in script
    assert "Cloud SQL" in script
    assert "gcloud builds" not in script


def test_read_only_verification_cannot_certify_database_connectivity() -> None:
    """Health and ready revisions are not mistaken for SQL acceptance."""
    script = (MANUAL / "03-verify-deployment.ps1").read_text(encoding="utf-8")
    assert "run services describe" in script
    assert "get-iam-policy" in script
    assert "allUsers" in script
    assert "Still REQUIRED:" in script
    assert "Cloud SQL SELECT" in script
    assert "run deploy" not in script
    assert "add-iam-policy-binding" not in script


def test_manual_runbook_separates_real_cloud_acceptance_from_ci() -> None:
    """Cloud Build and external source validation remain separately tracked."""
    guide = (ROOT / "docs" / "fnd04-manual-deployment.md").read_text(
        encoding="utf-8"
    )
    assert "FND-04 is NOT Done" in guide
    assert "FND-AUTO (#11)" in guide
    assert "20261031_0006" in guide
    assert "03-verify-deployment.ps1" in guide
    assert "Cloud SQL" in guide
