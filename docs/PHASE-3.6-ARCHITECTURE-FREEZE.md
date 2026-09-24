# Phase 3.6 — Architecture Freeze: Production Semantic RAG & Evaluation

Status: architecture baseline frozen before provider selection
Baseline commit: 2d48f9e
Date: 2026-09-24

## 1. Objective

Phase 3.6 introduces a production-capable semantic retrieval path and a measurable RAG promotion process while preserving Phase 3.5 invariants:

- workspace isolation;
- durable queued indexing;
- worker leases/retries;
- server-derived citations;
- provider abstraction;
- deterministic local fallback;
- no unmeasured semantic-quality claims.

Provider selection is deliberately deferred to the ChatGPT Work research packet.

## 2. Non-negotiable invariants

1. Every embedding is tied to a workspace, chunk, embedding profile, and index version.
2. Retrieval can only compare query and chunk embeddings from the same compatible profile/version.
3. A profile switch never silently mixes old and new model vectors.
4. Reindex is resumable and does not destroy the currently serving index until the replacement is ready.
5. The existing deterministic provider remains available for tests/development.
6. Production secrets are external configuration only.
7. Provider outage cannot corrupt the current serving index.
8. Semantic quality promotion requires versioned evaluation evidence.

## 3. New embedding profile concept

Introduce an application/domain concept similar to:

```text
EmbeddingProfile
- Id
- Name
- Provider
- Model
- Dimensions
- DistanceMetric
- Normalization
- IndexVersion
- Status: Building | Active | Retired | Failed
- CreatedAtUtc
- ActivatedAtUtc
```

The exact persistence entity can be adjusted during implementation, but these semantics are frozen.

Phase 3.6A enforces one deployment-wide Active profile. Phase 3.6B may generalize activation scope only if product requirements require per-workspace profiles. In every scope, only one profile/index version may serve as Active at a time.

## 4. Chunk embedding identity

The current one-vector-per-chunk model is insufficient for safe model migration.

Phase 3.6 target identity:

```text
(ChunkId, WorkspaceId, EmbeddingProfileId)
```

or an equivalent key that makes the profile/version explicit.

Embedding rows must carry:

- chunk ID;
- workspace ID;
- embedding profile ID;
- embedding vector;
- created/indexed timestamp.

A database constraint must continue to prevent cross-workspace chunk/embedding linkage.

## 5. pgvector dimension strategy

The application must not hardcode 64 dimensions in orchestration.

The vector store contract must receive or resolve:

- profile ID;
- dimensions;
- distance metric;
- index version.

For multiple embedding profiles, rows must not be queried through an approximate index built for an incompatible dimension/model profile.

Implementation may use a generic `vector` column plus profile-scoped expression/partial indexes, or another reviewed scheme with equivalent guarantees. The selected scheme must be proven on real PostgreSQL/pgvector before merge.

Phase 3.6 does not permit an unsafe in-place dimension change on the serving index.

## 6. Blue/green embedding migration

Profile/model change:

```text
Current Active Profile A
        |
        +-----------------------> remains serving
        |
        v
Create Profile B = Building
        |
        v
Reindex chunks to B
        |
        v
Build/validate B vector index
        |
        v
Run deterministic + offline evals
        |
      PASS
        |
        v
Atomically activate B
        |
        v
A becomes Retired
```

Failure while building B leaves A serving.

## 7. Provider boundary

Extend the embedding abstraction to expose a stable descriptor:

```text
EmbeddingProviderDescriptor
- Provider
- Model
- Dimensions
- Normalization
- Version
```

The result of an embedding request must be validated against the configured active/building profile.

No provider adapter may choose dimensions silently at runtime.

## 8. Failure classification

Provider failures must be classified at least as:

- transient/network;
- rate-limited;
- authentication/configuration;
- invalid input;
- provider/model mismatch;
- dimension mismatch;
- permanent/unsupported.

Queue retry policy may retry transient/rate-limit failures, but configuration/auth/dimension failures must fail fast enough to avoid wasteful retry loops.

## 9. Retrieval flow

```text
query
  |
  v
resolve active embedding profile
  |
  v
embed query with same profile
  |
  v
workspace + profile scoped candidate retrieval
  |
  v
lexical/vector fusion
  |
  v
retrieved-content policy
  |
  v
optional semantic reranker
  |
  v
document diversification
  |
  v
final top-K + trace metadata
```

If semantic embedding is unavailable, the fallback policy must be explicit. Phase 3.6 default recommendation is:

- do not silently query an incompatible vector profile;
- optionally use a deterministic lexical-only/development fallback when configured;
- surface degraded mode in telemetry/API metadata.

## 10. Reranker boundary

Retain `IRagReranker` semantics but allow an optional provider-backed reranker.

Required metadata:

- provider;
- model/version;
- candidate count;
- final K;
- latency;
- fallback used.

The deterministic Phase 3.5 reranker remains the fallback and test implementation.

## 11. Evaluation dataset

Add versioned repository data, for example:

```text
evals/rag/v1/
  dataset.json
  README.md
```

Each case should identify:

- case ID;
- query;
- expected relevant source/document/chunk labels;
- optional required facts;
- optional forbidden facts;
- tags/category;
- tenant/workspace fixture;
- dataset version.

Do not use private production user content in repository evaluation fixtures.

## 12. Deterministic evaluation gates

CI-safe metrics:

- Recall@K / hit-rate@K;
- Precision@K where labels support it;
- citation source correctness;
- tenant leakage = 0;
- unsafe retrieved-content filter cases;
- profile/version compatibility;
- queue/provider fallback behavior;
- p50/p95 local deterministic latency where stable enough for trend reporting.

Promotion thresholds are stored with the evaluation spec and must be changed explicitly in review.

## 13. Offline/model evaluation

Outside the deterministic CI gate:

- groundedness;
- faithfulness;
- answer relevance;
- context precision;
- context recall;
- citation correctness.

Every result must record:

- evaluation dataset version;
- embedding profile;
- reranker profile;
- answer model;
- prompt/runtime version;
- timestamp.

No model/provider is promoted solely from anecdotal examples.

## 14. Traceability

For each indexing/retrieval/agent run, telemetry or durable metadata must make it possible to determine:

- embedding provider/model/dimensions/profile/index version;
- reranker provider/model/version;
- retrieval candidate count;
- final K;
- filter count/reasons;
- fallback/degraded mode;
- indexing/retrieval/rerank latency.

Normal logs must not contain full private document contents or secrets.

## 15. Frontend scope

Minimal UI only:

- document/index status remains visible;
- semantic index/profile can be shown in an advanced/status view if useful;
- degraded retrieval may surface a non-alarming status;
- no provider secret entry forms in the browser.

Provider credentials/config belong in deployment/server configuration.

## 16. Phase 3.6 implementation order

1. Add embedding profile/index metadata model.
2. Refactor hardcoded dimensions out of application/vector interfaces.
3. Add profile-aware vector persistence/retrieval.
4. Add safe migration/reindex orchestration.
5. Add production provider adapter selected after research.
6. Add reranker provider boundary/fallback.
7. Add evaluation dataset schema and deterministic runner.
8. Add trace metadata/telemetry.
9. Add frontend status changes only if required.
10. Run full PostgreSQL/Docker/evaluation merge gate.

## 17. Explicit out of scope

Deferred to Phase 4+:

- general agent tool registry;
- arbitrary external actions;
- email/calendar/GitHub tool execution;
- human approval engine;
- workflow orchestration;
- tool secrets/credential vault;
- autonomous scheduled actions.

## 18. Merge blockers

Phase 3.6 cannot merge if any of the following are true:

- incompatible vector profiles can be mixed;
- active index can be destroyed before replacement validation;
- cross-workspace retrieval is possible;
- provider credentials are committed or logged;
- provider failure leaves documents falsely marked Ready;
- evaluation dataset/version is absent;
- deterministic evaluation gates are not reproducible;
- real PostgreSQL migration/index behavior is unverified.
