# BRF-02 — Use Gemini and Claude safely

> Story: [Trello BRF-02](https://trello.com/c/csVH6KCV/22-brief-brf-02-use-gemini-and-claude-safely) ·
> Gate G2 · Depends on FND-04 · Design v1.11 §10.9 · Decision D04 (what data may go to Claude)
>
> **Status:** implemented and unit-tested on branch `feature/BRF-02-08-brief-pipeline` (not
> committed). Not yet run against real Gemini or Claude (see [Open items](#open-items)).
> 10 November 2026. Overall flow: [BRIEF-ARCHITECTURE.md](../BRIEF-ARCHITECTURE.md).

*Route each AI task to Gemini or Claude through one interface, with budgets, versioned prompts and
injection protection, so model output is controlled and traceable.*

| Acceptance criterion | How it is met |
| --- | --- |
| Gemini drafts briefs; Claude handles code and conflicting evidence, per the v1.11 routing table. | `ROUTING_TABLE` in `routing.py` has one row per task of the design's matrix. A brief goes to Gemini. Claude gets code fixes and the critique of a brief whose evidence conflicts. One provider per task; a second one only for a named reason. |
| Every call logs provider, model, prompt version and tokens. | The gateway logs one `llm_call` line per call and the worker stores it in `knowledge.llm_calls`, with the routing revision, data classification, status and elapsed time. No prompt or response text is logged or stored. |
| Instructions hidden in ticket text are treated as data and flagged. | Ticket and evidence text go to the model inside `<untrusted_data>` blocks that the data cannot close. Instruction-like text is recorded on the brief (`injection_flags`) and logged by pattern name. The model has no tools, and its output is checked against the evidence whatever it says. |
| When the budget runs out, the result is PARTIAL with the evidence found, never invented advice. | The gateway refuses a call once the run's calls, tokens or seconds are spent and returns `llm_budget_exhausted`. The pipeline then publishes a `PARTIAL` brief with the evidence and risk factors and no advice. |

## How a call is made

```
 pipeline ──► ModelGateway.generate(task, evidence, run ledger)
                1. RoutingPolicy.primary(task, data classification)
                     provider configured? may it see this class of data?   no → llm_no_allowed_provider
                2. Budget: calls, tokens, seconds left?                    no → llm_budget_exhausted
                3. prompt = versioned template + evidence as <untrusted_data> blocks
                4. provider.generate(...)   never raises: OK | REFUSED | ERROR | TIMEOUT
                5. output must match the template's JSON Schema            no → ERROR schema_violation
                6. log llm_call, charge the budget, keep the call in the ledger
                7. ERROR or TIMEOUT → the one allowed fallback provider, once, same prompt and schema
                   REFUSED         → no second model (llm_refused)
```

### Routing table (S08-04, revision `routing-2026-11-10.1`)

| Task | Primary | Second provider, and when |
| --- | --- | --- |
| Ticket classification | Gemini | Claude on ambiguous labels |
| Brief draft | Gemini | Claude critique on `evidence_conflict` or `high_impact`; Claude takes over once after a Gemini error |
| Evidence critique | Claude | none |
| Root-cause hypotheses | Gemini | Claude when disputed or safety-critical |
| Code fix and regression test | Claude | none (Gemini only if a benchmark justifies it) |
| Enhancement approach | Gemini | Claude on cross-repository contracts |
| Knowledge extraction | Gemini | Claude on unresolved clusters |
| Advice analytics | no model | SQL and verified outcomes first |

Only the brief draft and the evidence critique have prompt templates today; the other rows are
ready for the DEFECT and ENHANCEMENT phases.

### Who may see which data (rule 1 of §10.9, decision D04)

Every run has a data classification (`LLM_DATA_CLASSIFICATION`, default `INTERNAL`). Each provider
has a ceiling. Gemini on Vertex AI stays inside the Google Cloud project, so its default ceiling is
`RESTRICTED`. **Claude receives nothing until both `CLAUDE_ROUTE` and
`LLM_CLAUDE_MAX_CLASSIFICATION` are set.** This is checked before any prompt is built. So with
`RESTRICTED` data and a Claude ceiling of `INTERNAL`, a Gemini outage does not fall back to Claude:
the brief is `PARTIAL` and the run can be repeated.

| `CLAUDE_ROUTE` | Where the request goes |
| --- | --- |
| `off` (default) | No Claude provider |
| `vertex` | Claude on Vertex AI (`AnthropicVertex`), inside the Google Cloud project |
| `anthropic` | The Anthropic API. An external transfer: needs D04 approval. Credentials from `ANTHROPIC_API_KEY` |

The Claude provider uses the Anthropic SDK with model `claude-opus-5-5`, structured output
(`output_config.format`) and `output_config.effort` (default `medium`); thinking stays adaptive.
On the Anthropic API route it also opts into server-side refusal fallback
(`CLAUDE_REFUSAL_FALLBACK`, default on): if the model declines for policy reasons, Anthropic re-runs
the request on another Claude model, and the call log records the model that answered. Set it to
`false` to turn that off.

### Budget and fallback (S08-05)

One budget per run: `LLM_MAX_CALLS_PER_RUN` (3), `LLM_MAX_TOKENS_PER_RUN` (60 000, input plus
output) and `LLM_MAX_SECONDS_PER_RUN` (90). Each call's timeout is the smaller of
`LLM_CALL_TIMEOUT_SECONDS` (45) and what is left of the run. Escalations are charged to the same
budget.

### Prompts (S08-06)

`prompts.py` holds the templates. Each has a name and a version (`brief.defect@v1`,
`brief.critique@v1`) that is recorded with every call and on the brief. Changing the wording means
raising the version.

### Injection and leakage (S08-07, S15-10)

| Layer | What it does |
| --- | --- |
| Isolation | Retrieved text is wrapped in `<untrusted_data>`; tags inside the data are neutralised; the system prompt says the blocks are data |
| No tools | The model gets no tools at all. `KnowledgeTools.dispatch` allows exactly two read-only tools and refuses and logs any other name |
| Detection | `injection.scan` finds seven patterns (override, system-prompt probe, role reassignment, text addressed to the assistant, action requests, exfiltration, chat markup). Findings are flagged, never obeyed |
| Output checks | Advice containing instructions, unknown links or secrets is removed (BRF-04). Model-written limitations and critique reasons are bounded and scrubbed |
| Logs | Pattern names and counts only; never the text |

### Second opinion (S08-10, S08-11)

After a draft, the pipeline asks for a critique only if a cited fix was reverted or a lesson is
disputed (`evidence_conflict`), or the risk band is `HIGH` (`high_impact`), and only if Claude may
see the data and the budget has a call left. The reviewer's verdict can only add doubt: an advice
item it finds unsupported or conflicting is marked `DISPUTED` and the disagreement is shown on the
brief. It never restores removed advice, never rewrites advice, and the two opinions are never
averaged.

### Comparing providers (S08-12)

`evaluation.py` runs labelled cases (`tests/intelligence/llm_cases/brief_cases.json`) through a
provider and reports output validity, citation validity, grounded recall, harmful advice,
abstention accuracy, tokens and time. The test suite runs it on the offline stand-in and on a
deliberately careless provider, to prove the harness tells them apart. To compare real providers:

```python
from app.llm.evaluation import EvaluationCase, evaluate

report = evaluate(GeminiProvider(project, location, model), cases)  # same for ClaudeProvider
print(report.summary())
```

## Code map

| Task | Where (`src/VisionIQ.Intelligence/app/llm/`) |
| --- | --- |
| S08-01 provider-neutral interface | `types.py` (`ModelProvider`, `TaskSpec`, `ModelResult`, `EvidenceContext`) |
| S08-02 Gemini provider | `providers/gemini.py` (Vertex AI REST, JSON response schema) |
| S08-03 Claude provider | `providers/claude.py` (Anthropic SDK, direct or on Vertex) |
| S08-04 routing table | `routing.py` |
| S08-05 budget and fallback | `gateway.py` (`Budget`, `ModelGateway`) |
| S08-06 prompt templates | `prompts.py` |
| S08-07 tool allowlist, injection isolation | `tools.py`, `injection.py`, `prompts.py` |
| S08-08 knowledge and evidence tools | `tools.py` (`retrieval.search`, `evidence.fetch_authorized_source`) |
| S08-10 review and escalation | `routing.py` (`escalation`), `gateway.py` (`escalate`), `app/brief/pipeline.py` |
| S08-11 cross-provider consistency | `consistency.py` |
| S08-12 output quality evaluation | `evaluation.py`, `tests/intelligence/llm_cases/` |
| S15-10 injection and leakage tests | `tests/intelligence/test_llm_injection.py` |
| Offline stand-in | `providers/offline.py` (`LLM_MODE=offline`; tests and local demos only) |
| Wiring | `factory.py`, `app/config.py` |

## How to test it

```powershell
python -m pytest tests/intelligence/test_llm_gateway.py tests/intelligence/test_llm_providers.py tests/intelligence/test_llm_injection.py tests/intelligence/test_llm_evaluation.py
```

77 tests: every routing branch, the D04 ceiling, one-call-by-default, the logged fields, fallback
and its limits, each budget limit, schema violations, provider failure states for Gemini and
Claude, the Claude request shape, the seven injection patterns, the tool allowlist, an injection
test ticket that is flagged and not obeyed, and the evaluation harness.

Completion evidence, locally (after the [walkthrough](../BRIEF-ARCHITECTURE.md#how-to-test-the-whole-flow-locally)):

```powershell
docker compose logs intelligence | Select-String llm_call          # provider, model, prompt version, tokens
docker compose exec postgres psql -U visioniq -d visioniq -c "SELECT task, role, provider, model_id, prompt_template_version, routing_revision, status, input_tokens, output_tokens FROM knowledge.llm_calls"
```

To see an injection ticket flagged, run `test_an_injection_ticket_is_flagged_and_its_instructions_are_not_followed`.

Against real Gemini (needs `gcloud auth application-default login` and the Vertex AI API):
set `LLM_MODE=live`, `GCP_PROJECT_ID`, `VERTEX_LOCATION` and `GEMINI_MODEL`, keep
`BRIEF_PUBLISH_MODE=record`, and repeat the walkthrough. Real ticket text then goes to Vertex AI in
your project, so use the synthetic demo history unless real data is approved.

## Open items

- No call has been made to real Gemini or Claude. The request and response handling follows the
  Vertex AI `generateContent` REST API and the Anthropic SDK, and is tested against fakes.
- D04 is undecided, so Claude is off. Until then a brief has no second opinion and no fallback.
- `GEMINI_MODEL` defaults to `gemini-2.5-flash`; confirm the model for the project and region.
- Budgets are in calls, tokens and seconds. A cost in money per run is not computed.
- Results are not cached (rule 5 of §10.9): every run calls the model again.
- `graph.neighbors` is not a tool yet (the authorized graph traversal is in the .NET API).
- The S08-12 cases are four synthetic ones. Engineer-labelled cases are needed before the routing
  table is called evaluated.
