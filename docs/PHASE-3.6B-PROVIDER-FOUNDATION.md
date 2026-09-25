# Phase 3.6B — Semantic Provider Foundation

Status: implementation complete pending final branch commit / CI
Base: Phase 3.6A `d75d08d`
Date: 2026-09-25

## Goal

Phase 3.6B Foundation adds the provider-neutral blue/green machinery required to evaluate and safely promote a production semantic embedding provider without replacing or corrupting the currently serving RAG index.

This foundation does **not** activate a paid semantic provider and does not claim semantic quality improvements. Live provider benchmarking remains an explicit, separately authorized step because it requires credentials and incurs provider usage/cost.

## Delivered

### Active vs Building profile separation

- `IServingEmbeddingProfileResolver` resolves only the deployment-wide Active profile.
- `IBuildEmbeddingProfileResolver` resolves only the Building profile.
- ordinary document ingestion always uses the Active profile;
- profile migration uses a separate stable-chunk build path;
- the database enforces at most one Active and at most one Building profile.

This prevents a Building profile from becoming serving traffic before promotion succeeds.

### Provider registry and capabilities

`IEmbeddingProvider` is now profile-agnostic and receives:

- embedding profile;
- explicit purpose: `Document` or `Query`;
- input batch;
- cancellation token.

Providers expose capabilities:

- provider name;
- supported dimensions;
- maximum batch inputs;
- optional token limit;
- purpose-routing support.

`IEmbeddingProviderRegistry` resolves providers by the persisted profile provider name and rejects unknown providers as non-retryable configuration failures.

### Provider configuration readiness

Providers that can become Active implement `IEmbeddingProviderConfigurationProbe`.

API `/ready` now requires all of:

- PostgreSQL ready;
- AI runtime ready;
- Active profile metadata + compatible HNSW ready;
- Active provider configured for the profile.

The local deterministic provider validates provider/model/dimensions.

The OpenAI provider additionally requires its server-side API key.

No live provider request is made by readiness.

### Stable-chunk Building profile path

`EmbeddingProfileBuildService`:

- resolves only the Building profile;
- provisions/verifies the candidate HNSW index;
- reuses existing persisted chunk IDs;
- finds only chunks missing the candidate profile vector;
- embeds in provider-capability bounded batches;
- never mixes chunks from different workspaces in one external provider request;
- writes additional profile-specific vectors without replacing the serving chunk corpus;
- reports candidate coverage.

This keeps Active vectors intact while Building vectors are produced.

### Profile-scoped HNSW provisioner

`PostgresVectorIndexProvisioner`:

- supports the current float32 `vector` + HNSW + cosine path;
- rejects dimensions above 2,000;
- creates deterministic profile-scoped partial HNSW indexes;
- validates index readiness by profile and dimensions;
- drops an index only for a Retired profile.

### Structured evaluation evidence

`RagEvaluationEvidence` persists:

- dataset version;
- embedding profile ID;
- provider/model;
- dimensions;
- index version;
- HitRate@K;
- MeanRecall@K;
- MeanPrecision@K;
- citation correctness;
- tenant leakage count;
- evaluation kind;
- runner version;
- completion timestamp;
- optional offline semantic metrics.

Evidence is bound to the exact profile/model/index version.

### Benchmark runner

`RagBenchmarkRunner`:

- evaluates an explicit profile without activating it;
- uses profile-aware retrieval;
- persists deterministic evidence;
- records measured provider input tokens when supplied;
- estimates cost only from measured usage + explicitly supplied pricing metadata;
- evaluates dataset thresholds and reports `ThresholdsPassed`;
- supports `TenantForbiddenSources` so cross-tenant retrieval is measured instead of hardcoded to zero;
- never auto-activates a provider.

### Promotion policy

`RagPromotionPolicy` requires deterministic evidence to match:

- profile;
- provider;
- model;
- dimensions;
- index version;
- required dataset version;
- metric thresholds;
- zero tenant leakage.

Production configuration additionally requires offline semantic evidence.

Offline semantic evidence must match the same profile **and dataset version** and can require minimum:

- groundedness;
- answer relevance;
- faithfulness;
- context precision;
- context recall.

### Atomic activation

`PostgresEmbeddingProfileActivationStore` performs the final swap under PostgreSQL `SERIALIZABLE` isolation.

It locks the relevant corpus/profile tables, then rechecks:

- candidate is still Building;
- exactly one Active profile exists;
- deterministic evidence still exists;
- optional offline evidence still exists;
- no knowledge document is Queued/Processing;
- candidate vector coverage equals Ready chunk count;
- candidate profile HNSW exists and matches dimensions.

Only after those checks does one transaction perform:

```text
old Active -> Retired
Building    -> Active
```

No retired vectors or indexes are deleted during activation.

The partial unique Active-profile database index remains the final concurrency guard.

## OpenAI structural adapter

A mock-tested OpenAI adapter is implemented for the first benchmark profile:

```text
provider: openai
model: text-embedding-3-small
dimensions: 1536
distance: cosine
```

The adapter:

- sends `model`, `input`, `encoding_format=float`, and `dimensions`;
- validates response model/count/index/dimensions;
- records API usage token counts;
- classifies rate limits / server failures / timeouts as retryable;
- classifies auth/config/profile mismatch as non-retryable;
- requires the API key only from server configuration;
- never exposes the key to the frontend or normal telemetry.

`docker-compose.yml` maps `OPENAI_API_KEY` only into `OpenAiEmbedding__ApiKey`.

No real OpenAI API key was used and no live OpenAI embedding request was executed during Foundation validation.

## Tenant hardening

Two Phase 3.6B-specific controls were added during senior review:

1. Building-profile provider calls are single-workspace batches. Data belonging to two workspaces is never combined in the same provider request.
2. Benchmark tenant leakage is measured through explicit tenant-forbidden source labels. It is no longer hardcoded to zero.

Existing vector storage and retrieval continue to filter and validate `WorkspaceId`.

## Database migration

`20260924203604_Phase36BProviderFoundation` adds the Phase 3.6B persistence needed for:

- one Building profile invariant;
- evaluation evidence;
- provider promotion support.

Phase 3.6A migrations remain unchanged and continue to preserve the original 64-dimensional serving vectors.

## CI changes

GitHub Actions now runs on `phase-*` pushes as well as main/feature/fix branches.

Backend CI exposes a PostgreSQL test connection to Phase 3.6B integration tests after applying EF migrations to a real `pgvector/pgvector:pg16` service.

Normal local unit tests do not require PostgreSQL.

## Validation evidence

### Backend

- Debug: 90/90 passed
- Release: 90/90 passed

### PostgreSQL / pgvector

A dedicated Docker-network integration database was migrated from zero through all migrations, including Phase 3.6B.

Real PostgreSQL tests:

- successful candidate HNSW provisioning + atomic Active/Building swap: PASS
- failed activation with missing evidence preserves old Active profile and Building candidate: PASS

Result: 2/2 PostgreSQL-specific integration tests passed.

### Frontend

- tests: 8/8 passed
- ESLint: passed
- Next.js 16.3.6 production build: passed
- npm audit: 0 vulnerabilities

### AI runtime

- pytest: 5/5 passed
- one existing Starlette/AnyIO deprecation warning remains non-blocking

### EF Core

- idempotent migration script generation: passed
- real PostgreSQL migration history includes `20260924203604_Phase36BProviderFoundation`

### Docker

- Phase 3.6B API image: built successfully
- AI image: built successfully

### Live readiness — local baseline

With the baseline Active profile:

```json
{
  "status": "ready",
  "database": "ready",
  "aiRuntime": "ready",
  "embeddingProfile": "local-deterministic-64-v1",
  "embeddingProfileReady": true,
  "embeddingProviderReady": true
}
```

HTTP: `200`

### Live readiness — OpenAI profile without key

A temporary 1536-dimensional OpenAI Active profile and matching HNSW index were created in the isolated validation database. No API key was supplied.

Result:

```json
{
  "status": "not_ready",
  "database": "ready",
  "aiRuntime": "ready",
  "embeddingProfile": "openai-small-1536-readiness-test",
  "embeddingProfileReady": true,
  "embeddingProviderReady": false
}
```

HTTP: `503`

The temporary profile/index were removed and the local baseline Active profile was restored after the proof.

## Foundation limitations

Not claimed by this release:

- a live OpenAI embedding benchmark;
- a live Voyage benchmark;
- a selected semantic-provider winner;
- semantic quality improvement;
- model-based offline groundedness results;
- production provider credentials;
- production OpenAI profile activation;
- semantic reranking.

Those require explicit provider credentials/cost authorization and the provider benchmark gate.

## Next gate — Phase 3.6B Provider Benchmark

Before a semantic provider may become Active:

1. expand/freeze the evaluation corpus;
2. configure an explicitly authorized provider key through deployment secrets;
3. build the candidate profile without disturbing Active;
4. run deterministic benchmark;
5. run required offline semantic evaluation;
6. record latency/cost/error metrics;
7. review privacy/data-retention account settings;
8. prove provider outage/degraded behavior;
9. require zero tenant leakage;
10. activate only if promotion policy passes.

The Foundation itself never auto-promotes a candidate.
