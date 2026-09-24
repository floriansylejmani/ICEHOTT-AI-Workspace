# Work Packet — ChatGPT Work — Phase 3.6

## Role
Independent research and audit. Do not implement repository code in this packet.

## Project
ICEHOTT AI Workspace
Repository: https://github.com/floriansylejmani/ICEHOTT-AI-Workspace
Baseline commit: 2d48f9e — feat: harden production rag pipeline

## Objective
Research current production semantic RAG choices and produce a dated evidence-based memo that can be converted into architecture acceptance criteria.

## Questions to answer
1. Which current embedding providers/models are credible production options for this architecture?
2. Dimensions, input limits, batching limits, rate limits, latency considerations, and pricing.
3. Data retention/privacy/security and enterprise controls.
4. Migration implications when embedding dimensions/model versions change.
5. Current reranking options and tradeoffs: hosted reranker vs local cross-encoder vs deterministic fallback.
6. Provider outage/fallback strategies.
7. Evaluation best practices for recall@K, precision@K, groundedness, faithfulness, citation correctness, and promotion gates.
8. Current observability/tracing recommendations for RAG pipelines.
9. Prompt-injection and retrieved-content risks relevant to semantic RAG.
10. Recommend 2–3 architecture-compatible options without declaring a winner unless the evidence and project constraints clearly justify it.

## Deliverable
A sourced memo with:
- date checked;
- provider/model comparison table;
- API/SDK/documentation links;
- cost/latency/privacy caveats;
- migration implications;
- evaluation methodology;
- security implications;
- unresolved questions.

## Constraints
- Do not assume vendor facts from memory; verify current documentation.
- Do not expose or request secrets.
- Do not modify local code.
- Separate measured/documented facts from recommendations.
- Do not claim semantic quality without benchmark/evaluation evidence.
