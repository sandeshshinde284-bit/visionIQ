# syntax=docker/dockerfile:1
# Build context is the repository root (see docker-compose.yml) because the requirement files live there:
# docker compose build intelligence

FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PORT=8000

WORKDIR /app

# m-6: runtime dependencies only, pinned and hash-verified (no test tools in the image).
# The runtime image deliberately excludes db/ migration code.
COPY requirements-runtime.lock.txt ./requirements-runtime.lock.txt
RUN pip install --require-hashes -r requirements-runtime.lock.txt

COPY src/VisionIQ.Intelligence/app ./app
# FND-05: shared event schemas and topic topology, validated on produce and consume.
COPY contracts/events ./contracts/events

# Run as a non-root user.
RUN useradd --create-home --uid 10001 appuser
USER 10001

EXPOSE 8000

# The slim image has no curl, so the health check uses Python itself.
HEALTHCHECK --interval=15s --timeout=3s --start-period=15s --retries=3 \
    CMD ["python", "-c", "import os, urllib.request; urllib.request.urlopen('http://127.0.0.1:%s/health' % os.environ.get('PORT', '8000'), timeout=3)"]

# Shell form on purpose: ${PORT} is expanded at start-up (Cloud Run sets PORT).
CMD ["sh", "-c", "exec python -m uvicorn app.main:app --host 0.0.0.0 --port ${PORT:-8000}"]
