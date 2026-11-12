# BRF-04 — Every point has a source

> Story: [Trello BRF-04](https://trello.com/c/90jCJFQQ/24-brief-brf-04-every-point-has-a-source) ·
> Gate G2 · Depends on BRF-03 · Design v1.11 §7.4.1
>
> **Status:** implemented and unit-tested on branch `feature/BRF-02-08-brief-pipeline` (not
> committed). 10 November 2026. Overall flow: [BRIEF-ARCHITECTURE.md](../BRIEF-ARCHITECTURE.md).

*As an engineer, I want every point in the brief to link to the ticket, PR or commit it came from,
so that I can check it myself.*

| Acceptance criterion | How it is met |
| --- | --- |
| Each advice item cites at least one source; unsupported text is removed or marked. | `validate` keeps an advice item only if a source it cites supports it (three checks, below). Anything else is removed, counted, and the brief says how many statements were removed. An item a second reviewer doubts is kept but marked `DISPUTED`. |
| Each advice item has its own ID, so it can be rated later. | Kept items are numbered `ADV-001`, `ADV-002`… within the brief. The ids are stored in `knowledge.brief_advice`, sent in `analysis.completed`, shown on the ticket and used by BRF-06 ratings. |
| When history is thin, the brief says so instead of guessing. | Vague ticket, no history, weak evidence and no supported advice each give a `PARTIAL` brief with its own reason code and a plain-language limitation. No model is asked when there is nothing to ground advice on. |

## The evidence check (S10-07)

A model's draft is only a proposal. For each advice item it must give the evidence ids it relies on
and, for each id, a short quote copied from that evidence. `app/brief/validate.py` then requires,
for at least one cited id:

| Check | Catches | Removal reason |
| --- | --- | --- |
| 1. The id is in the evidence given to the model | A fabricated or mistyped id | `unknown_evidence` |
| 2. The quote appears word for word in that evidence (ignoring case, spacing and quote style; at least 12 characters) | A real id cited for something the source never says | `quote_not_in_source` |
| 3. The statement shares specific terms with that evidence | A real quote attached to an unrelated claim | `statement_not_about_source` |

Checking only that the id exists would not be enough: a model can cite a real ticket for a claim
that ticket never makes. Sources that fail are dropped from the item's citations; if none is left,
the item is removed.

The text a quote is checked against is what the model was shown: the source's title, its snippet
and its notes (for example "a fix linked to this ticket was later reverted"). A quote that itself
reads like an instruction supports nothing.

Items are also removed when they are malformed (`malformed`), contain instructions, a link that is
not one of the evidence links, or a secret (`unsafe_content`), repeat an earlier item
(`duplicate`), or exceed eight items (`over_limit`).

Only sources with a link an engineer can open are citable: tickets, PRs, commits and design pages.
A lesson (LRN-10) is derived knowledge with no link of its own, so it is given to the model as a
fact and counted as a risk factor, but advice cannot rest on it alone.

Advice is never rewritten or repaired by code, and a second model never restores a removed item.

## Advice ids and states (§7.4.1)

Each item stores `advice_id`, `kind`, `statement`, `evidence_ids`, the supporting quotes and
`evidence_state`. A new brief only ever has `UNVERIFIED` or `DISPUTED`: nothing in VisionIQ marks
advice as correct. The engineer's rating (BRF-06) is a separate dimension. Briefs and their advice
are immutable; a correction is a new run with a new brief.

## Missing or inconclusive evidence (S10-10)

| Situation | State and reason | What the ticket shows |
| --- | --- | --- |
| The ticket is not readable in the knowledge base | `BLOCKED` / `ticket_not_readable` | Why, and that no history could be matched |
| Too few specific terms | `PARTIAL` / `ticket_too_vague` | What to add: error message, component, repro steps |
| Nothing similar found | `PARTIAL` / `no_history_found` | Risk `Not enough data`, no advice |
| Results from one search only, no code-path match | `PARTIAL` / `weak_evidence` | The advice that passed the check, plus the warning |
| The model's advice failed the check | `PARTIAL` / `no_supported_advice` | The evidence and risk, and how many statements were removed |
| A search lane failed | reasons list the lane | "Results may be incomplete" |

## Code map

| Task | Where |
| --- | --- |
| S10-07 validate per-advice evidence | `src/VisionIQ.Intelligence/app/brief/validate.py` |
| S10-10 missing or inconclusive evidence | `app/brief/pipeline.py` (states and reasons), `app/knowledge/retrieval.py` (`judge`) |
| Advice ids | `validate.py` (numbering), `app/brief/schema.py` (`Advice`), `knowledge.brief_advice` |
| Second-reviewer doubt | `app/llm/consistency.py` |

## How to test it

```powershell
python -m pytest tests/intelligence/test_brief_validate.py tests/intelligence/test_brief_pipeline.py
```

35 tests. The first file covers the checks one by one: a fabricated id, a real id with an invented
quote, a real quote with an unrelated claim, typographic quotes, short quotes, mixed good and bad
sources, malformed items, instructions, links and secrets, duplicates and the item limit. The
second covers the thin-history paths and the "removed statements" note.

Completion evidence: after the [local walkthrough](../BRIEF-ARCHITECTURE.md#how-to-test-the-whole-flow-locally),
every advice item on the ticket comment has a `Source:` link that opens the cited ticket, and:

```powershell
docker compose exec postgres psql -U visioniq -d visioniq -c "SELECT a.advice_id, a.kind, a.evidence_ids, a.evidence_state FROM knowledge.brief_advice a JOIN knowledge.briefs b USING (brief_id) ORDER BY b.created_at DESC, a.advice_id LIMIT 8"
```

## Open items

- Check 3 is a word-overlap test, not a judgement of meaning. It stops unrelated claims; it cannot
  tell whether advice draws the right conclusion from its source. Design §10.9 calls for an
  entailment review, which needs labelled cases to tune.
- The quote is checked against the snippet the model saw (about 320 characters per source), not
  the whole source.
- Evidence links are whatever ingestion stored as the source URL. Whether each link opens for the
  engineer depends on their own ADO or Confluence access.
