"""S08-07: text from tickets, pages and commits is data. Find instructions hidden in it.

Detection never changes what the model may do: the model has no tools and its output is checked
against the evidence whatever it says. A finding is recorded on the brief so the engineer can
see that the source tried to steer the assistant.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

# Fixed codes only: the matched text itself is never logged or stored.
_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    (
        "override_instructions",
        re.compile(
            r"(?i)\b(ignore|disregard|forget|override)\s+(all\s+|any\s+|the\s+|your\s+)?"
            r"(previous|prior|above|earlier|system|preceding)\s+(instructions?|prompts?|rules?)"
        ),
    ),
    ("system_prompt_probe", re.compile(r"(?i)\b(system\s+prompt|developer\s+message)\b")),
    (
        "role_reassignment",
        re.compile(
            r"(?i)\b(you\s+are\s+now|act\s+as\s+(an?\s+)?(ai|assistant|admin|system)|"
            r"new\s+instructions?\s*:|from\s+now\s+on\s+you)"
        ),
    ),
    (
        "addressed_to_assistant",
        re.compile(
            r"(?i)\b(ai|assistant|llm|chatbot|visioniq|gemini|claude)\s*[,:]\s*"
            r"(please\s+)?(you\s+must|do\s+not|always|never|approve|merge|delete|post|reply|write)"
        ),
    ),
    (
        "action_request",
        re.compile(
            r"(?i)\b(approve|merge|complete|close|delete)\s+(this|the)\s+"
            r"(pr|pull\s+request|ticket|branch|work\s+item)\s+(automatically|without\s+review|now)"
        ),
    ),
    (
        "exfiltration_request",
        re.compile(
            r"(?i)\b(reveal|print|send|include|leak|exfiltrate)\s+(all\s+|the\s+|your\s+)?"
            r"(secrets?|credentials?|api\s+keys?|tokens?|passwords?|environment\s+variables?)"
        ),
    ),
    ("chat_markup", re.compile(r"(?i)<\s*/?\s*(system|assistant|untrusted_data)\b|\[/?INST\]")),
)

_DATA_TAG = re.compile(r"(?i)<\s*/?\s*untrusted_data\b[^>]*>")


@dataclass(frozen=True)
class InjectionFinding:
    """Where instruction-like text was found (``source``: "ticket" or an evidence id)."""

    source: str
    pattern: str


def scan(source: str, text: str) -> list[InjectionFinding]:
    """One finding per pattern that matches, in a stable order."""
    return [InjectionFinding(source, code) for code, pattern in _PATTERNS if pattern.search(text)]


def looks_like_instruction(text: str) -> bool:
    return any(pattern.search(text) for _, pattern in _PATTERNS)


def neutralize(text: str) -> str:
    """Stop data from closing the block that marks it as data."""
    return _DATA_TAG.sub("[tag removed]", text)
