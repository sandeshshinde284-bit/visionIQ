"""The Vertex AI verification tool: each failure mode gives a concrete fix."""

from app.knowledge.embeddings import VertexGeminiEmbedder
from app.knowledge.vertex_check import QUERY, SAME_MEANING, UNRELATED, run_checks


def basis(index, nudge=0.0):
    vector = [0.0] * 768
    vector[index] = 1.0
    vector[(index + 1) % 768] = nudge
    return vector


class Response:
    def __init__(self, status, body):
        self.status_code = status
        self._body = body

    def json(self):
        return self._body


class FakeVertex:
    """Answers per sentence; ``vectors`` maps text -> vector, ``status`` forces an error."""

    def __init__(self, vectors=None, status=200, message=""):
        self.vectors = vectors or {}
        self.status = status
        self.message = message

    def post(self, url, json, timeout):
        if self.status != 200:
            return Response(self.status, {"error": {"message": self.message}})
        text = json["instances"][0]["content"]
        return Response(200, {"predictions": [{"embeddings": {"values": self.vectors[text]}}]})


SEMANTIC = {SAME_MEANING: basis(0, 0.2), QUERY: basis(0), UNRELATED: basis(5)}


def make(session):
    return lambda: VertexGeminiEmbedder(
        "my-gcp-project", "europe-west1", session=session, sleep=lambda _: None, max_retries=0
    )


def test_a_working_semantic_model_passes_every_step():
    steps = run_checks(make(FakeVertex(SEMANTIC)))

    assert [s.name for s in steps] == [
        "credentials and configuration",
        "Vertex AI access",
        "pinned dimension",
        "semantic matching",
        "latency",
    ]
    assert all(s.passed for s in steps)


def test_forbidden_explains_how_to_enable_the_api_and_grant_the_role():
    steps = run_checks(make(FakeVertex(status=403, message="Permission denied on resource")))

    failed = steps[-1]
    assert (failed.name, failed.passed) == ("Vertex AI access", False)
    assert "Permission denied" in failed.detail
    assert "aiplatform.googleapis.com" in failed.fix and "roles/aiplatform.user" in failed.fix


def test_a_model_that_does_not_separate_meaning_fails():
    flat = dict.fromkeys(SEMANTIC, basis(0))

    steps = run_checks(make(FakeVertex(flat)))

    assert (steps[-1].name, steps[-1].passed) == ("semantic matching", False)


def test_missing_configuration_fails_with_setup_guidance():
    def broken():
        raise KeyError("GCP_PROJECT_ID")

    (step,) = run_checks(broken)

    assert not step.passed and "GCP_PROJECT_ID" in step.fix
