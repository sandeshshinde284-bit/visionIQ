"""LRN-08 (S07-01): retrieval chunks with provenance.

Strategy: convert HTML to text, keep each source section separate (title, description, repro
steps, review comments...), split on paragraph then sentence boundaries into chunks of at most
MAX_CHARS with a small overlap, and prefix every chunk with a context header (kind and title) so
a chunk is meaningful on its own. Chunk keys are deterministic: re-indexing the same revision
produces the same chunks.
"""

from __future__ import annotations

import hashlib
import html
import re
from dataclasses import dataclass
from html.parser import HTMLParser

CHUNKER_VERSION = "chunker-v1"
MAX_CHARS = 1200
OVERLAP_CHARS = 150
_BLOCKS = {
    "p",
    "div",
    "br",
    "li",
    "tr",
    "h1",
    "h2",
    "h3",
    "h4",
    "h5",
    "h6",
    "pre",
    "blockquote",
    "ul",
    "ol",
}


class _Text(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.parts: list[str] = []
        self.skip = 0

    def handle_starttag(self, tag: str, attrs) -> None:
        if tag in {"script", "style"}:
            self.skip += 1
        elif tag in _BLOCKS:
            self.parts.append("\n- " if tag == "li" else "\n")

    def handle_endtag(self, tag: str) -> None:
        if tag in {"script", "style"}:
            self.skip = max(0, self.skip - 1)
        elif tag in _BLOCKS:
            self.parts.append("\n")

    def handle_data(self, data: str) -> None:
        if not self.skip:
            self.parts.append(data)


def html_to_text(value: str) -> str:
    """Plain text from (possibly) HTML; scripts/styles dropped, blocks become line breaks."""
    if "<" not in value:
        text = html.unescape(value)
    else:
        parser = _Text()
        parser.feed(value)
        parser.close()
        text = "".join(parser.parts)
    lines = [re.sub(r"[ \t\u00a0]+", " ", line).strip() for line in text.splitlines()]
    return re.sub(r"\n{3,}", "\n\n", "\n".join(lines)).strip()


@dataclass(frozen=True)
class Section:
    name: str
    text: str


@dataclass(frozen=True)
class Chunk:
    chunk_key: str
    chunk_index: int
    section: str
    text: str
    content_sha256: str


def _split_long(paragraph: str, limit: int) -> list[str]:
    if len(paragraph) <= limit:
        return [paragraph]
    sentences = re.split(r"(?<=[.!?])\s+", paragraph)
    pieces: list[str] = []
    current = ""
    for sentence in sentences:
        while len(sentence) > limit:  # a single huge "sentence" (logs, stack traces)
            pieces.extend([current] if current else [])
            current = ""
            pieces.append(sentence[:limit])
            sentence = sentence[limit:]
        if current and len(current) + 1 + len(sentence) > limit:
            pieces.append(current)
            current = sentence
        else:
            current = f"{current} {sentence}".strip()
    if current:
        pieces.append(current)
    return pieces


def chunk(
    item_key: str, revision: str, kind_label: str, title: str, sections: list[Section]
) -> list[Chunk]:
    """Deterministic, context-prefixed chunks of at most MAX_CHARS characters."""
    header = f"{kind_label}: {title}".strip()[:300]
    # -40: margin for the "[section]" line and newlines wrapped around the body below.
    budget = MAX_CHARS - len(header) - 40
    chunks: list[Chunk] = []
    for section in sections:
        body = section.text.strip()
        if not body:
            continue
        # Leave room so the overlap from the previous chunk always fits in front of a piece.
        piece = budget - OVERLAP_CHARS - 2
        parts = re.split(r"\n\s*\n", body)
        paragraphs = [p for part in parts for p in _split_long(part.strip(), piece) if p]
        current = ""
        bodies: list[str] = []
        for paragraph in paragraphs:
            if current and len(current) + 2 + len(paragraph) > budget:
                bodies.append(current)
                tail = current[-OVERLAP_CHARS:]
                # Drop the leading partial word so the overlap doesn't start mid-token.
                tail = tail[tail.find(" ") + 1 :] if " " in tail else tail
                current = (
                    f"{tail}\n\n{paragraph}"
                    if len(tail) + 2 + len(paragraph) <= budget
                    else paragraph
                )
            else:
                current = f"{current}\n\n{paragraph}" if current else paragraph
        if current:
            bodies.append(current)
        for text_body in bodies:
            text = f"{header}\n[{section.name}]\n{text_body}"
            digest = hashlib.sha256(text.encode()).hexdigest()
            index = len(chunks)
            key = hashlib.sha256(f"{item_key}|{revision}|{index}|{digest}".encode()).hexdigest()
            chunks.append(Chunk(key, index, section.name, text, digest))
    return chunks
