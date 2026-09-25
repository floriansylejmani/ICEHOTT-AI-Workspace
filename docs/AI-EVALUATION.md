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
