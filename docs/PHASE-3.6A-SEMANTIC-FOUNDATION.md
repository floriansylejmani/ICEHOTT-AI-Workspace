# Phase 3.6A — Semantic RAG Foundation

Status: branch-ready foundation; not yet a production semantic-provider release
Branch: `phase-3.6a-semantic-foundation`
Baseline: `2b60ee2`
Date: 2026-09-24

## Goal

Phase 3.6A removes the fixed 64-dimensional vector assumption from the RAG architecture and introduces the profile/version/evaluation primitives required for safe semantic-provider adoption in Phase 3.6B.

It deliberately keeps the existing deterministic 64-dimensional runtime as the active provider so schema, migration, retrieval, queue, tenant, readiness, and evaluation behavior can be proven before introducing vendor credentials or production semantic costs.

## Delivered

- `EmbeddingProfile` domain model with explicit lifecycle:
  - Building
  - Active
  - Retired
  - Failed
- invalid profile lifecycle transitions rejected in Domain code
- pgvector-compatible dimension bounds: 1..16000
- `EmbeddingProfileDescriptor` carried by the provider boundary
- embedding failure classification and retryability
- profile-aware `IVectorStore`
- profile-aware indexing and retrieval
- provider/model/version/index-version/dimension telemetry tags
- generic pgvector `vector` storage for multi-dimension profiles
- embedding identity changed to `(ChunkId, EmbeddingProfileId)`
- workspace ownership retained on every embedding row
- baseline 64-dimensional vectors backfilled to the seeded local profile
- profile-scoped partial HNSW index for the active baseline profile
- single Active profile enforced by a partial unique database index
- API readiness verifies:
  - PostgreSQL connectivity
  - AI runtime readiness
  - persisted profile metadata compatibility
  - matching profile-scoped HNSW index
- versioned deterministic RAG evaluation dataset under `evals/rag/v1`
- deterministic evaluation metrics:
  - HitRate@K
  - Mean Recall@K
  - Mean Precision@K
  - citation correctness
  - tenant leakage count
- explicit prompt-injection/forbidden-source fixture in the evaluation set

## Current serving profile

```text
Key: local-deterministic-64-v1
Provider: icehott-ai-runtime
Model: deterministic-64d
Dimensions: 64
Version: 1
IndexVersion: 1
DistanceMetric: cosine
Normalization: unit
Status: Active
```

This remains a development/test baseline. Phase 3.6A does not claim semantic production quality.

## Database migration

### `20260924153012_Phase36EmbeddingProfiles`

- creates `embedding_profiles`
- seeds the existing local deterministic profile
- converts `knowledge_chunk_embeddings.Embedding` from `vector(64)` to generic `vector`
- adds `EmbeddingProfileId`
- backfills existing rows to the baseline profile
- changes embedding PK to `(ChunkId, EmbeddingProfileId)`
- adds profile FK
- preserves workspace ownership
- adds workspace/profile index
- creates baseline profile-scoped HNSW index

### `20260924174042_Phase36ProfileInvariants`

- replaces the ordinary status index with a partial unique index
- guarantees only one deployment-wide `Active` embedding profile in Phase 3.6A

Phase 3.6B may generalize activation scope only if product requirements require per-workspace provider profiles.

## Upgrade proof with existing data

A temporary PostgreSQL database was cloned from the real Phase 3.5 development database.

Before migration, a real `vector(64)` embedding row was inserted.

After applying both Phase 3.6A migrations:

```text
embeddings = 1
profiled = 1
min_dims = 64
max_dims = 64
EmbeddingProfileId = local baseline profile ID
profile match = true
```

The migration output showed `UPDATE 1`, proving an existing embedding row was backfilled rather than only testing an empty schema.

## Single-active invariant proof

A second `Active` profile insert was attempted against real PostgreSQL after migration.

Result:

```text
single-active invariant PASS: second Active profile rejected
```

Building/Retired/Failed profiles remain allowed.

## Readiness negative proof

The Phase 3.6A API Docker image was connected to a temporary upgraded PostgreSQL database and the normal AI service.

With the compatible baseline HNSW index present:

```json
{
  "status": "ready",
  "database": "ready",
  "aiRuntime": "ready",
  "embeddingProfile": "local-deterministic-64-v1",
  "embeddingProfileReady": true
}
```

HTTP status: `200`

The HNSW index was then dropped while database and AI remained healthy.

Result:

```json
{
  "status": "not_ready",
  "database": "ready",
  "aiRuntime": "ready",
  "embeddingProfile": "local-deterministic-64-v1",
  "embeddingProfileReady": false
}
```

HTTP status: `503`

This proves readiness detects a missing/incompatible serving vector index rather than checking profile metadata alone.

## Evaluation dataset

Path:

`evals/rag/v1/dataset.json`

The dataset is synthetic and non-private. It contains:

- support/refund knowledge
- security/MFA knowledge
- audit-retention knowledge
- a malicious retrieved-instruction fixture

Current deterministic thresholds are intentionally strict for this small synthetic regression dataset:

- HitRate@K = 1.0
- Mean Recall@K = 1.0
- Mean Precision@K = 1.0
- Citation correctness = 1.0
- Tenant leakage = 0

These are regression gates for the deterministic baseline, not claims about real-world semantic quality.

## Phase 3.6A boundary

Not implemented here:

- production semantic embedding provider
- provider credential handling beyond existing server configuration patterns
- separate Active vs Building provider resolution
- blue/green profile activation transaction/orchestrator
- automatic creation of HNSW indexes for arbitrary future dimensions/profiles
- hosted/model semantic reranker
- provider cost/latency comparison
- model-based groundedness/faithfulness evaluation
- semantic provider promotion

Those are Phase 3.6B responsibilities.

## Final validation — September 24, 2026

- Backend Debug: 35/35 tests passed
- Backend Release: 35/35 tests passed
- Frontend: 8/8 tests passed
- Frontend ESLint: passed
- Next.js production build: passed
- npm install audit in isolated worktree: 0 vulnerabilities
- FastAPI: 5/5 tests passed
- EF Core idempotent migration script: generated successfully
- Docker API image for Phase 3.6A: built successfully
- Phase 3.5 -> Phase 3.6A PostgreSQL upgrade with an existing vector row: passed
- Existing vector row backfill: 1/1 row preserved and assigned to the baseline profile
- Single Active profile PostgreSQL invariant: passed
- HNSW index positive readiness proof: HTTP 200
- HNSW index missing negative readiness proof: HTTP 503 while database and AI runtime stayed ready
- git diff --check: passed
- Temporary test database/container/artifacts: removed

## Phase 3.6B required architecture

Phase 3.6B must introduce separate concepts for:

```text
Serving Profile (Active)
        +
Build Profile (Building)
```

The ingestion/reindex path may target the Building profile while retrieval continues using the Active profile. Only after vector indexing and evaluation pass may one transaction retire the prior Active profile and activate the Building profile.

No provider adapter may silently replace the serving profile.

## Merge gate for 3.6A

Required before merge to main:

- backend Debug tests pass
- backend Release tests pass
- frontend tests/lint/build pass
- AI tests pass
- idempotent EF migration script generates
- Phase 3.5 -> 3.6A real PostgreSQL upgrade passes with existing vector data
- single Active profile invariant passes on PostgreSQL
- HNSW positive/negative readiness proof passes in Docker
- evaluation dataset gate passes
- Docker API build passes
- no temp artifacts/secrets
- independent diff review
- branch commit only; main merge requires lead review
