# ICEHOTT AI Evaluation Plan

ICEHOTT will treat evaluation as a product feature, not an afterthought.

## RAG metrics
- Groundedness
- Answer relevance
- Context precision
- Context recall
- Citation correctness

## Phase 3 / 3.5 deterministic checks
- Embeddings have the expected 64 dimensions.
- Non-empty embeddings are normalized.
- Workspace knowledge cannot be retrieved by a non-member.
- Indexed documents transition through Queued / Processing / Ready.
- Chat citations come from retrieved chunks and persist with conversation history.
- Structure-aware chunking preserves overlap.
- Retrieval expands candidates before deterministic reranking.
- Reranking limits single-document dominance in the initial selection.
- Known instruction-override / prompt-exfiltration patterns are filtered from retrieved context.
- Queue ownership prevents stale workers from completing or retrying another worker's lease.
- Reindexing does not create duplicate active jobs.
- Frontend tests verify citation rendering, queued/processing status, polling, reindex, and retrieval UX.

These checks validate RAG mechanics and failure handling. They do not establish production semantic quality; groundedness, recall, precision, citation correctness, and provider promotion still require a versioned evaluation dataset.

## Phase 3.6A / 3.6B provider evaluation
- Embedding profiles bind provider/model/dimensions/index version.
- Deterministic evidence records dataset version, HitRate@K, Mean Recall@K, Mean Precision@K, citation correctness, and tenant leakage.
- Benchmark cases can declare tenant-forbidden sources; tenant leakage is measured from returned sources rather than assumed to be zero.
- Dataset thresholds are evaluated explicitly and exposed as a benchmark pass/fail result.
- Promotion rejects evidence for a different profile, index version, or dataset version.
- Production promotion can require separate offline semantic evidence for groundedness, answer relevance, faithfulness, context precision, and context recall.
- Benchmark evidence is persisted even when thresholds fail so failed runs remain auditable.
- Provider usage/cost estimates use measured token counts when the provider returns them.
- A benchmark runner never activates a candidate automatically.
- Live provider benchmarks are opt-in and are not part of deterministic CI.

Phase 3.6B Foundation validates provider contracts and blue/green promotion mechanics with mocks and real PostgreSQL/pgvector. It does not claim semantic superiority for OpenAI, Voyage, Cohere, or any other provider without a live benchmark.

Phase 3.6B evaluation dataset v2 expands the deterministic corpus to 13 synthetic documents and 18 cases across support, security, retention, reliability, billing, identity, data management, API, residency, prompt-injection safety, and tenant isolation. The exact corpus is hash-locked in `evals/rag/v2/dataset.sha256`. Passing v2 is a prerequisite for the opt-in live provider benchmark; it is not by itself a semantic-quality claim.

On 2026-09-25, the authorized OpenAI candidate benchmark completed against `rag-v2`. Deterministic HitRate@K, recall, precision, and citation correctness were all 1.00 with zero tenant leakage. A separate bounded `gpt-6-luna` offline judge evaluated 17 positive cases; groundedness, answer relevance, faithfulness, context precision, context recall, and citation correctness were all 1.00 with zero tenant leakage. The production promotion policy passed. Measured total provider cost was $0.00100536 against a $0.05 approved cap. This evidence establishes that this candidate met the frozen ICEHOTT promotion gate for this dataset/run; it is not a general claim of provider superiority.

## Agent metrics
- Task completion rate
- Tool-call success rate
- Approval frequency
- Retry/error rate
- Median and p95 latency
- Tokens and cost per run

## Evaluation workflow
1. Maintain versioned evaluation datasets.
2. Run deterministic checks in CI where possible.
3. Run model-based evaluations separately from unit tests.
4. Compare prompt/model/router versions before promotion.
5. Never claim quality improvements without measured evidence.
