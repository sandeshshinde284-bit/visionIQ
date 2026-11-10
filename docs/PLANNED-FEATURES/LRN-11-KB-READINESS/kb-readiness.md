# Knowledge-base readiness (LRN-11)

Proves the knowledge base can be trusted before BRIEF work starts (master design §6.16 KB-5, §11.1.4).
Run on a bootstrapped database:

    VISIONIQ_DB_INTEGRATION=1 VISIONIQ_BENCHMARK_DIR=out \
      PYTHONPATH=src/VisionIQ.Intelligence python -m pytest tests/intelligence/test_kb_readiness_db.py

Each case writes its fixture, a **snapshot ID** (fingerprint of the exact evidence state tested: item,
latest revision, deletion and access list) and its assertions to `kb-readiness-results.json`. CI keeps
it, with the LRN-09 retrieval benchmark, as the `learn-evidence-<commit>` artifact.

| Case | Fixture | Assertions |
|---|---|---|
| **S15-15 permission revocation** | Access moved to another team, ticket deleted, access list unverified | The canonical evidence query (the exact .NET `EvidenceQueryService` SQL and rule) denies all three; search and retrieval return nothing; no lesson is shown |
| **Two PRs, one reverted** | Ticket fixed by two PRs, one later reverted | Both fix links kept, neither verified; every lesson's cause is unverified; retrieval flags the revert and claims no verified cause |
| **Connector outage** | Archive and Vertex AI down after a ticket changed; database down | The last good chunks are still served **and labelled as possibly outdated**; a failed search lane is reported; without a database (no access check) there is no answer (fail closed) |
| **Malicious ticket text** | Script and an instruction addressed to an AI in a ticket | Scripts never reach the index; the text is flagged as data, not instructions |
| **Interrupted re-index** | Model change interrupted by an embedding outage | No item becomes unfindable; the re-run completes with one model, never mixed |

The live provider checks (ADO membership, Confluence permission check) are covered by the .NET test
suites (B-5/B-6, LRN-02); this suite covers the stored-evidence side end to end.
