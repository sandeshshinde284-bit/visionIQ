"""LRN-08 / LRN-03: redact secrets and personal data from text BEFORE it is indexed or embedded.

Raw source copies stay in the restricted archive; only redacted text is chunked, stored for
search and sent to an embedding model. Redaction is conservative (it prefers removing a token
over leaking one) and versioned, so a stricter policy re-indexes everything.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

REDACTION_VERSION = "text-redaction-v1"

_PATTERNS: tuple[tuple[str, re.Pattern[str], str], ...] = (
    (
        "private_key",
        re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----", re.S),
        "",
    ),
    ("token", re.compile(r"\beyJ[\w-]{8,}\.[\w-]{8,}\.[\w-]{8,}"), ""),  # JWT
    ("token", re.compile(r"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})"), ""),
    ("token", re.compile(r"\bxox[abprs]-[A-Za-z0-9-]{10,}"), ""),  # Slack
    ("token", re.compile(r"\bAIza[0-9A-Za-z_-]{35}\b"), ""),  # Google API key
    ("token", re.compile(r"\bAKIA[0-9A-Z]{16}\b"), ""),  # AWS access key ID
    ("token", re.compile(r"\b[a-z2-7]{52}\b"), ""),  # classic Azure DevOps PAT (base32)
    (
        "auth_header",
        re.compile(r"(?i)\b(authorization\s*[:=]\s*(?:bearer|basic)\s+)[A-Za-z0-9._~+/=-]{8,}"),
        r"\1",
    ),
    (
        "secret",
        re.compile(
            r"(?i)\b((?:password|passwd|pwd|secret|client_secret|api[_-]?key|apikey|access[_-]?key|"
            r"account[_-]?key|auth[_-]?token|token|sig|sharedaccesssignature|pat)\s*[:=]\s*)"
            r"(\"[^\"]*\"|'[^']*'|[^\s;,&]+)"
        ),
        r"\1",
    ),
    ("email", re.compile(r"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"), ""),
    ("phone", re.compile(r"(?<![\w+])\+\d{1,3}[\s.-]?\(?\d{1,4}\)?(?:[\s.-]?\d{2,4}){2,4}\b"), ""),
)
# Long mixed-case alphanumeric strings look like opaque credentials (pure hex, e.g. commit
# SHAs, is kept).
_OPAQUE = re.compile(r"(?<![\w/.-])[A-Za-z0-9+/_-]{40,}={0,2}(?![\w/.-])")
_CARD = re.compile(r"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)")


@dataclass(frozen=True)
class Redacted:
    text: str
    counts: dict[str, int]


def _luhn(digits: str) -> bool:
    total = 0
    for index, char in enumerate(reversed(digits)):
        value = int(char)
        if index % 2:
            value = value * 2 - 9 if value > 4 else value * 2
        total += value
    return total % 10 == 0


def redact(text: str) -> Redacted:
    """Replace secrets and personal data with [REDACTED:<kind>] markers."""
    counts: dict[str, int] = {}

    def count(kind: str, n: int) -> None:
        if n:
            counts[kind] = counts.get(kind, 0) + n

    for kind, pattern, keep in _PATTERNS:
        replacement = (keep + f"[REDACTED:{kind}]") if keep else f"[REDACTED:{kind}]"
        text, n = pattern.subn(replacement, text)
        count(kind, n)

    def opaque(match: re.Match[str]) -> str:
        value = match.group(0)
        is_hex = re.fullmatch(r"[0-9a-fA-F]+", value) is not None
        mixed = (
            any(c.islower() for c in value)
            and any(c.isupper() for c in value)
            and any(c.isdigit() for c in value)
        )
        if is_hex or not mixed:
            return value
        count("token", 1)
        return "[REDACTED:token]"

    text = _OPAQUE.sub(opaque, text)

    def card(match: re.Match[str]) -> str:
        digits = re.sub(r"\D", "", match.group(0))
        if 13 <= len(digits) <= 19 and _luhn(digits):
            count("card", 1)
            return "[REDACTED:card]"
        return match.group(0)

    text = _CARD.sub(card, text)
    return Redacted(text, counts)
