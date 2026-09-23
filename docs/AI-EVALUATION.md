# ICEHOTT AI Evaluation Plan

ICEHOTT will treat evaluation as a product feature, not an afterthought.

## RAG metrics
- Groundedness
- Answer relevance
- Context precision
- Context recall
- Citation correctness

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
