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
