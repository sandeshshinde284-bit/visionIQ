# ENH-01 — Guidance for building the enhancement

> Story: [Trello ENH-01](https://trello.com/c/oDaB81xx) · Gate G2 · Depends on BRF-03, BRF-05,
> LRN-07 · Design v1.11 §8.5, §3.2
>
> **Status:** implemented and tested with scripted and offline models and against PostgreSQL,
> 15 November 2026. **Not yet run with a real model or posted to a real ticket** (see
> [Not verified yet](#not-verified-yet)). ENH-02 (optional starter PR) is not part of this.
> Overall flow: [BRIEF-ARCHITECTURE.md](../BRIEF-ARCHITECTURE.md).

*As an engineer building an enhancement, I want guidance on contracts, risks and past changes,
so that I avoid breaking existing behaviour.*

| Acceptance criterion | How it is met |
| --- | --- |
| The guidance covers the eight sections of design §8.5. | The sections and their order are fixed in code (`app/brief/guidance.py`). The stored guidance always lists all eight; a model can neither add nor drop one, and an answer that names an unknown section is rejected as a whole by the gateway's output check. |
| A section with no evidence is marked missing, never filled with guesses. | Every item goes through the brief's evidence check (`app/brief/validate.py`): its quote must be found word for word in the source it cites. A section with no surviving item has status `missing` and is shown as missing on the ticket. |
| Each item cites its source and has an advice ID, like the defect brief. | An item **is** an advice item: `ADV-nnn`, a statement, evidence ids and quotes. It only adds the section it belongs to. Ratings (BRF-06) work on it unchanged. |

## The eight sections

| # | Section | What goes in it |
| --- | --- | --- |
| 1 | Acceptance-criteria gaps | Something the ticket does not specify that a similar change had to decide |
| 2 | Contracts and APIs touched | What other code depends on. **Written by code** from the dependency graph (below) |
| 3 | Deployment and migration risk | A rollout, data-migration or compatibility problem the evidence shows |
| 4 | Related past changes | An earlier change to the same code or behaviour, and what happened to it |
| 5 | Do / don't | What a past change shows works, or fails (for example a reverted change) |
| 6 | Recommended sequence | An order of work the evidence supports |
| 7 | Tests to add | A test the evidence shows was needed or missing |
| 8 | Reviewer decisions | A choice the evidence leaves open |

## How it works

An enhancement goes through the **same pipeline as a defect** (`build_brief`): the ticket,
retrieval, numbered evidence, injection checks, predicted scope, risk band, the evidence check,
storage and publication. Whatever is fixed there is fixed for both. Three things differ:

| Step | For an enhancement |
| --- | --- |
| Which tickets | The pickup policy's `enhancement_types` (BRF-01). No new setting. The API sends the decision as `ticket_kind` on `analysis.requested`; a request without it is a defect. |
| Evidence | Recorded references to the files in scope are added as citable evidence of kind `code_dependency` (PROP-02). |
| Model task | `ENHANCEMENT_APPROACH`, already a row of the v1.11 routing matrix (Gemini first), with prompt `brief.enhancement@v1`. A second opinion (Claude) is asked for when another repository depends on this one, the routing table's `cross_repo_contract` reason. |

### Contracts and APIs touched (PROP-02)

Design §8.5: dependents may be named *only when evidence supports them*. So this section is not
left to a model. `BriefSources.dependents` reads the dependency graph (LRN-07), which records
explicit references only:

- a Python import, between the two files;
- a .NET project reference, between the two project files, so a source file is matched through
  the innermost project whose folder contains it;
- another repository that declares a dependency on this one.

Only the latest scan of a repository counts, and only evidence the run's access scope may read.
The result becomes evidence, and code writes one item per target from it. Those items pass the
same evidence check as a model's. A model may add items to the section, but only with a quote
from the evidence, so it cannot name a dependent nobody recorded.

Limits, stated on the ticket when they apply:

- For C# the graph knows **project-level** references, not which class uses which.
- A file the ticket only names (no folder) is looked up only when the history knows exactly one
  file with that name. An ambiguous name is left out: the dependents of the wrong project would
  be a guess.
- No recorded reference is **not** proof that nothing depends on a file. The guidance says so.

### On the ticket (PROP-03)

The API renders the stored guidance as *VisionIQ enhancement guidance*: the status and risk as
for a brief, then the eight sections numbered and in order. An item shows its advice id, kind,
source link and any second-review note. A section nothing supports is shown as *missing*; it is
never dropped. A defect brief is rendered exactly as before.

### When it falls short

| Situation | Result |
| --- | --- |
| The ticket is not readable | `BLOCKED`, all eight sections missing, no model call |
| No model is available | `PARTIAL`: the code-written contract items are still given; the rest is missing |
| Nothing is supported | `PARTIAL` / `no_supported_advice`, all eight sections missing |
| Thin history | `PARTIAL` / `weak_evidence`, with what is supported |

## Contracts changed

Both are additive; nothing new is required.

| Contract | Change |
| --- | --- |
| `contracts/events/analysis.requested.v1.schema.json` | Optional `ticket_kind`: `defect` or `enhancement` |
| `contracts/briefs/brief.v1.schema.json` | `kind`, `guidance` (the eight sections) and `section` on an advice item |

The event contract is strict on both sides, so **the API and Intelligence are deployed
together** for this change: an older Intelligence rejects a request that carries `ticket_kind`.
No database migration: the brief is stored as a document and the request event is already kept
with each run.

## Code map

| Where | What |
| --- | --- |
| `app/brief/guidance.py` | The sections, dependency evidence, the code-written contract items |
| `app/brief/pipeline.py` | The enhancement branch of `build_brief` |
| `app/brief/history.py` | `dependents`: the graph queries and their grouping |
| `app/brief/validate.py` | The evidence check, with sections |
| `app/llm/prompts.py` | `brief.enhancement@v1` and its output rules |
| `src/VisionIQ.Api/Features/Briefs/` | `BriefDocument`, `BriefCommentRenderer.AppendGuidance` |
| `src/VisionIQ.Api/Features/Events/EventContracts.cs` | `ticket_kind` on the request |

## How to try it locally

1. In the pickup policy, list the work item types that are enhancements:
   `"enhancement_types": ["User Story", "Feature"]`. A type cannot be both a defect and an
   enhancement.
2. Move such a ticket into a pickup state, or post to the development endpoint
   `/dev/analysis-requests` with `"ticket_kind": "enhancement"`.
3. With `LLM_MODE=offline` the guidance is assembled from the history alone: related past
   changes, a "don't" for a reverted change, and the contract items.

Automated tests:

```powershell
python -m pytest tests/intelligence/test_enhancement_guidance.py -q
# against PostgreSQL (after python -m db.manage all):
$env:VISIONIQ_DB_INTEGRATION = "1"
python -m pytest tests/intelligence/test_enhancement_guidance_db.py -q
dotnet test tests/VisionIQ.Api.Tests --filter "FullyQualifiedName~BriefCommentRendererTests|FullyQualifiedName~EventContractTests"
```

## Not verified yet

- **Guidance from a real model.** The tests use scripted and offline models. How well Gemini
  fills the sections from real history is not known until it is run.
- **A real comment on a real ticket.** The renderer is tested; the post to Azure DevOps is the
  brief's existing path and has not yet run live either.
- **Dependency data for a real repository.** The section depends on what the code scan recorded.
  If the scan holds no dependency links for a repository, the section is honestly missing.

## Open items

- The card's PROP tasks were marked *proposed, not yet approved*, and report delivery (D11) is
  open. This implements them as proposed; a change there changes this.
- Class-level dependents for C# need the scanner to record them (LRN-06 / LRN-07).
- ENH-02, the optional starter PR, is not started. Guidance alone completes the enhancement path.
