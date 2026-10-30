# One-shot DB migration/verification image; do not deploy as an HTTP runtime.
FROM python:3.12-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1
WORKDIR /app
COPY requirements.txt ./requirements.txt
RUN pip install -r requirements.txt
COPY db ./db
RUN useradd --create-home --uid 10001 appuser
USER 10001
CMD ["python", "-m", "db.manage", "all"]
