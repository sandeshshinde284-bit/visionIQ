# Vertex AI setup for search embeddings (D08)

VisionIQ embeds redacted text with **Gemini on Vertex AI** (`gemini-embedding-001`, 768 dimensions;
master design §10.8: Gemini on Vertex AI is available). Only redacted chunk text and redacted search
queries are sent; raw evidence never leaves the restricted archive.

## 1. Choose project and region

Use the Google Cloud project that hosts VisionIQ. Pick a region where `gemini-embedding-001` is
offered (check the current Vertex AI model availability list; availability changes) and, ideally, the
same region as Cloud SQL.

    export GCP_PROJECT_ID=<project-id>
    export VERTEX_LOCATION=<region>        # e.g. europe-west3

## 2. Enable the API

    gcloud services enable aiplatform.googleapis.com --project "$GCP_PROJECT_ID"

## 3. Grant access (least privilege)

The identity that runs the indexer and the retriever needs `roles/aiplatform.user` on the project:

    gcloud projects add-iam-policy-binding "$GCP_PROJECT_ID" \
      --member "serviceAccount:<indexer-sa>@$GCP_PROJECT_ID.iam.gserviceaccount.com" \
      --role roles/aiplatform.user

The same service account also needs read access to the raw evidence bucket (it already has it for
ingestion jobs if you reuse that account; otherwise `roles/storage.objectViewer` on that bucket only).

## 4. Credentials

- **Locally:** `gcloud auth application-default login` (your user needs the same role).
- **Cloud Run Jobs:** attach the service account above to the job; no keys or key files.

## 5. Verify (one command)

    PYTHONPATH=src/VisionIQ.Intelligence python -m app.knowledge.vertex_check

Expected:

    PASS  credentials and configuration: model vertex:gemini-embedding-001@768
    PASS  Vertex AI access: first call ... ms
    PASS  pinned dimension: 768 dimensions, norm 1.000000
    PASS  semantic matching: same meaning 0.xx vs unrelated 0.yy
    PASS  latency: median ... ms over 5 query calls
    Vertex AI embeddings are ready for LRN-08.

Each FAIL prints the fix (API not enabled or missing role → 403; model not in the region → 404;
quota → 429). The semantic step proves the model matches **the same meaning in different words**,
which is what LRN-08 promises and what the offline test embedder cannot do. Set `VERTEX_CHECK_JSON=1`
to also print the result as JSON for the acceptance record.

## 6. Index and measure

    EMBEDDING_PROVIDER=vertex PYTHONPATH=src/VisionIQ.Intelligence python -m app.knowledge.index_cli

Then re-run the LRN-09 benchmark on a labelled sample of real history (docs/retrieval.md) before
making any quality or latency claim.

## Cost and limits

Each indexed chunk is one embedding request (`EMBEDDING_BATCH_SIZE`, default 1); only changed items
are re-embedded. Review Vertex AI pricing and quotas for your region before the first full index run.
