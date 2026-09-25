# Phase 3.6B-7 — Live Provider Benchmark

Status: harness implemented; live execution blocked until explicit paid-API authorization and credentials are configured
Provider adapter under test: OpenAI `text-embedding-3-small`, 1536 dimensions
Dataset: `rag-v2`

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
ICEHOTT_LIVE_BENCHMARK_MAX_COST_USD
```

Optional:

```text
ICEHOTT_OPENAI_BASE_URL
ICEHOTT_OPENAI_TIMEOUT_SECONDS
```

Provider pricing is operator-supplied metadata rather than hard-coded so a dated benchmark records the price assumption used for that run.

## Dry-run evidence

The v2 corpus currently resolves to:

- 13 synthetic documents;
- 12 primary-workspace chunks;
- 1 foreign-workspace chunk;
- 18 benchmark queries;
- 2 expected document-embedding requests at the current 64-input application batch cap;
- 18 expected query-embedding requests;
- approximately 20 provider requests in total.

Dry-run verifies the frozen dataset SHA-256 and performs no provider network call.

## Live flow

1. validate explicit cost authorization and secrets;
2. migrate a dedicated fresh PostgreSQL/pgvector benchmark database;
3. seed isolated primary and foreign synthetic workspaces;
4. create an OpenAI 1536-dimensional Building profile;
5. provision the profile-scoped HNSW index;
6. embed all ready synthetic chunks with the Building profile;
7. verify complete vector coverage;
8. run all `rag-v2` retrieval cases with that same Building profile;
9. persist deterministic evaluation evidence;
10. record measured token usage, elapsed time, estimated cost, and threshold result;
11. write a secret-free local benchmark artifact;
12. leave the candidate Building and perform no activation.

## Output

Live artifacts are written under:

```text
artifacts/benchmarks/
```

That path is git-ignored.

The artifact contains the run ID, dataset version/hash, candidate profile metadata, coverage, evaluation evidence ID, deterministic metrics, tenant leakage, measured input tokens, duration, estimated cost, approved cost cap, and release-gate result.

## Promotion boundary

A deterministic live provider benchmark is necessary but is not sufficient to activate a semantic provider.
Production policy still requires reviewed offline semantic evidence for:

- groundedness;
- answer relevance;
- faithfulness;
- context precision;
- context recall.

A provider candidate must also keep tenant leakage at zero and remain within the explicitly approved cost envelope.

## Current blocker

At the time this harness was implemented, the local machine did not have:

- an OpenAI API key environment variable;
- a dedicated benchmark database connection;
- paid-API opt-in;
- pricing metadata;
- approved benchmark cost cap;
- a running Docker engine.

Therefore no paid provider call has been made and no semantic-quality PASS is claimed.
