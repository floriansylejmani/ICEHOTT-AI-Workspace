# Phase 3.6B-7 — Live Provider Benchmark

Status: live deterministic + offline semantic promotion evidence PASS; candidate remains Building and activation is a separate reviewed operation
Provider benchmarked: OpenAI `text-embedding-3-small`, 1536 dimensions
Offline semantic judge: OpenAI `gpt-6-luna`
Dataset: `rag-v2`
Evidence date: 2026-09-25

## Purpose

This gate exercises a real semantic embedding provider without disturbing the currently Active profile.
The benchmark is intentionally separate from serving traffic and never activates a candidate automatically.

## Safety invariants

- live mode requires an exact paid-API opt-in string;
- the API key is read only from server environment variables;
- the connection string is read only from a benchmark-specific environment variable;
- the database name must contain `benchmark`;
- the benchmark database must be fresh: zero users, workspaces, and knowledge documents;
- ordinary CI never makes a live provider call;
- no API key, connection string, document body, or secret is written to benchmark output;
- the candidate remains `Building` after the run;
- activation is always a separate reviewed operation.

## Commands

No-cost preflight:

```powershell
dotnet run --project backend/tools/ICEHOTT.Benchmarks/ICEHOTT.Benchmarks.csproj -c Release -- --dry-run
```

Live execution:

```powershell
dotnet run --project backend/tools/ICEHOTT.Benchmarks/ICEHOTT.Benchmarks.csproj -c Release -- --live
```

## Required live environment

```text
ICEHOTT_BENCHMARK_CONNECTION
ICEHOTT_LIVE_PROVIDER_BENCHMARK_OPT_IN=I_UNDERSTAND_THIS_USES_PAID_API
OPENAI_API_KEY or ICEHOTT_OPENAI_API_KEY
ICEHOTT_OPENAI_EMBEDDING_USD_PER_MILLION_INPUT_TOKENS
ICEHOTT_OPENAI_EVAL_INPUT_USD_PER_MILLION_TOKENS
ICEHOTT_OPENAI_EVAL_OUTPUT_USD_PER_MILLION_TOKENS
ICEHOTT_LIVE_BENCHMARK_MAX_COST_USD
```

Optional:

```text
ICEHOTT_OPENAI_BASE_URL
ICEHOTT_OPENAI_TIMEOUT_SECONDS
ICEHOTT_OPENAI_EVAL_MODEL
ICEHOTT_OPENAI_EVAL_TIMEOUT_SECONDS
ICEHOTT_OPENAI_EVAL_MAX_INPUT_BYTES
ICEHOTT_OPENAI_EVAL_MAX_OUTPUT_TOKENS
```

Provider pricing is operator-supplied metadata rather than hard-coded so a dated benchmark records the price assumption used for that run.

## Dry-run evidence

The v2 corpus currently resolves to:

- 13 synthetic documents;
- 12 primary-workspace chunks;
- 1 foreign-workspace chunk;
- 18 benchmark queries;
- 2 expected document-embedding requests at the current 64-input application batch cap;
- 36 expected query-embedding requests across deterministic and offline-semantic passes;
- 17 expected semantic-judge requests;
- 1 explicit safety negative-control case that is excluded from positive retrieval denominators but still gates forbidden content, citation safety, and tenant leakage.

Dry-run verifies the frozen dataset SHA-256 and performs no provider network call.

## Live flow

1. validate explicit cost authorization and secrets;
2. migrate a dedicated fresh PostgreSQL/pgvector benchmark database;
3. seed isolated primary and foreign synthetic workspaces;
4. create an OpenAI 1536-dimensional Building profile;
5. provision the profile-scoped HNSW index;
6. embed all ready synthetic chunks with the Building profile;
7. verify complete vector coverage;
8. run all `rag-v2` deterministic retrieval cases with that same Building profile;
9. persist deterministic evaluation evidence;
10. run 17 positive semantic cases through the bounded OpenAI judge while keeping the safety negative-control outside the judge;
11. persist offline semantic evidence for groundedness, answer relevance, faithfulness, context precision, and context recall;
12. meter document embeddings, both query passes, judge input/output tokens, duration, and total measured cost;
13. validate deterministic evidence + offline evidence with the production promotion policy;
14. write a secret-free local benchmark artifact;
15. leave the candidate Building and perform no activation.

## Output

Live artifacts are written under:

```text
artifacts/benchmarks/
```

That path is git-ignored.

The artifact contains the run ID, runner versions, dataset version/hash, candidate profile metadata, coverage, deterministic evidence, offline semantic evidence, tenant leakage, complete embedding/judge token usage, duration, conservative preflight cost estimate, measured total cost, approved cost cap, promotion-policy result, and release-gate result.

## Promotion boundary

A deterministic live provider benchmark is necessary but is not sufficient to activate a semantic provider.
Production policy still requires reviewed offline semantic evidence for:

- groundedness;
- answer relevance;
- faithfulness;
- context precision;
- context recall.

A provider candidate must also keep tenant leakage at zero and remain within the explicitly approved cost envelope.

## 2026-09-25 live evidence

Final full-gate run: `a4ae8e07-93e5-4715-85ac-253354b5e77b`.

Deterministic evidence:

- HitRate@K: 1.00
- Mean Recall@K: 1.00
- Mean Precision@K: 1.00
- Citation correctness: 1.00
- Tenant leakage: 0

Offline semantic evidence across 17 positive cases:

- Groundedness: 1.00
- Answer relevance: 1.00
- Faithfulness: 1.00
- Context precision: 1.00
- Context recall: 1.00
- Citation correctness: 1.00
- Tenant leakage: 0

Provider metering:

- document embedding input tokens: 370
- query embedding input tokens across both evaluation passes: 368
- total embedding input tokens: 738
- semantic judge input tokens: 5,741
- semantic judge output tokens: 833
- conservative preflight maximum cost: $0.02413056
- measured total provider cost: $0.00100536
- approved cap: $0.05

Promotion policy: PASS.

The benchmark candidate remained `Building`; `activationPerformed=false`. This is deliberate. Benchmark eligibility and production activation are separate reviewed operations, and no benchmark code path may activate automatically.
