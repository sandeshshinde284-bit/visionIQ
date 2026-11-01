# One-shot DB migration/verification image; do not deploy as an HTTP runtime.
FROM python:3.12-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1
WORKDIR /app
# m-6: runtime dependencies only, pinned and hash-verified (no test tools in the image).
COPY requirements-runtime.lock.txt ./requirements-runtime.lock.txt
RUN pip install --require-hashes -r requirements-runtime.lock.txt
COPY db ./db
RUN useradd --create-home --uid 10001 appuser
USER 10001
CMD ["python", "-m", "db.manage", "all"]
