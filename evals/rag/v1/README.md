# ICEHOTT RAG Evaluation Dataset v1

This folder contains synthetic, non-private fixtures for deterministic RAG regression tests.

The dataset is versioned because retrieval behavior must be compared against a stable target before an embedding or reranking profile is promoted.

## Deterministic gates

- HitRate@K
- Mean Recall@K
- Mean Precision@K
- Citation source correctness
- Tenant leakage count
- Forbidden/untrusted retrieved source exclusion

The current dataset intentionally includes a prompt-injection-style document. It must never appear in final retrieved context or citations.

## Scope

These tests validate retrieval mechanics, safety filtering, source attribution, and regression behavior. They do **not** establish production semantic quality.

Groundedness, faithfulness, answer relevance, model-based context precision/recall, and provider comparisons remain offline promotion evaluations.
