"""Structured (JSON) logging, written to stdout so Docker and Cloud Logging can collect it."""

import logging
import os
import sys

import structlog


def configure_logging(level: str = "INFO", fmt: str = "json") -> None:
    """Configure the standard library and structlog: one JSON object per line, or readable text.

    ``fmt="text"`` (LOG_FORMAT=text) renders single readable lines for local runs.
    """
    numeric_level = getattr(logging, level.upper(), logging.INFO)
    renderer = (
        structlog.dev.ConsoleRenderer(colors=False)
        if fmt.lower() == "text"
        else structlog.processors.JSONRenderer()
    )
    logging.basicConfig(format="%(message)s", stream=sys.stdout, level=numeric_level, force=True)
    structlog.configure(
        processors=[
            structlog.contextvars.merge_contextvars,
            structlog.processors.add_log_level,
            structlog.processors.TimeStamper(fmt="iso", utc=True),
            structlog.processors.StackInfoRenderer(),
            structlog.processors.format_exc_info,
            renderer,
        ],
        wrapper_class=structlog.make_filtering_bound_logger(numeric_level),
        logger_factory=structlog.PrintLoggerFactory(),
        cache_logger_on_first_use=False,
    )


def configure_from_environment() -> None:
    """Configure command-line tools from LOG_LEVEL (default INFO) and LOG_FORMAT (json or text)."""
    level = os.environ.get("LOG_LEVEL") or "INFO"
    configure_logging(level, os.environ.get("LOG_FORMAT") or "json")
