# Phase 3.6B — Semantic Provider Architecture Freeze

Status: architecture freeze before provider credentials
Base: d75d08d (Phase 3.6A)
Date: 2026-09-24

## 1. Goal

Phase 3.6B enables production semantic embedding providers without allowing provider/model changes to corrupt or silently replace the currently serving RAG index.

This phase is split into two release gates:

- **3.6B-Foundation** — provider-neutral blue/green orchestration, index provisioning, profile resolution, provider capabilities, activation transaction, and benchmark harness. No external credentials required.
- **3.6B-Provider Benchmark** — one or more provider adapters are exercised with explicitly authorized credentials and cost, evaluated against the versioned ICEHOTT dataset, and only then considered for activation.

No provider is selected by architecture alone.

## 2. Serving vs Building profiles

The application must distinguish:

```text
ServingEmbeddingProfile
- exactly one Active deployment-wide profile
- used by every retrieval/query request

BuildingEmbeddingProfile
- zero or one Building profile for the initial Phase 3.6B implementation
- used only by reindex/build operations
- never used for serving retrieval until activation succeeds
```

The current partial unique database index continues to guarantee at most one Active profile.

Phase 3.6B-Foundation should also enforce at most one Building profile unless a later scaling requirement justifies concurrent builds.

## 3. Provider abstraction refactor

The Phase 3.6A `IEmbeddingProvider.Profile` singleton cannot safely support one Active and one Building profile at the same time.

Replace it with a provider router/registry contract conceptually equivalent to:

```csharp
public enum EmbeddingPurpose
{
    Document = 1,
    Query = 2
}

public sealed record EmbeddingProviderCapabilities(
    string Provider,
    IReadOnlySet<int> SupportedDimensions,
    int MaxBatchInputs,
    int? MaxInputTokens,
    bool SupportsPurposeRouting);

public interface IEmbeddingProvider
{
    string Provider { get; }
    EmbeddingProviderCapabilities Capabilities { get; }

    Task<EmbeddingBatch> EmbedAsync(
        EmbeddingProfileDescriptor profile,
        EmbeddingPurpose purpose,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}

public interface IEmbeddingProviderRegistry
{
    IEmbeddingProvider Resolve(string provider);
}
```

Exact type names may vary, but the semantics are frozen.

## 4. Profile resolution

Add repository/application boundaries conceptually equivalent to:

```csharp
IEmbeddingProfileRepository
- GetActiveAsync()
- GetBuildingAsync()
- FindAsync(id)
- AddAsync(profile)

IServingEmbeddingProfileResolver
- ResolveAsync()

IBuildEmbeddingProfileResolver
- ResolveAsync()
```

Retrieval must call only the serving resolver.

Reindex/build orchestration must call only the build resolver.

No code path may infer serving/building status from configuration strings alone.

## 5. Provider profile compatibility

Before a provider request:

- provider name must match the profile;
- requested dimensions must be supported by provider capabilities;
- dimensions must be <= 16,000 storage limit;
- for the current float32 HNSW serving path, dimensions must be <= 2,000;
- Document vs Query purpose must be passed explicitly;
- provider response count/dimensions must match request/profile.

Provider mismatch is non-retryable.

Network/rate-limit failures may be retryable.

Authentication/configuration/dimension incompatibility is non-retryable.

## 6. Batch strategy

Batch size moves out of `KnowledgeIndexingProcessor` constants.

Effective batch size:

```text
min(
    configured application cap,
    provider MaxBatchInputs,
    provider/model cap
)
```

The provider adapter may split further if required, but the application must never exceed advertised provider capabilities.

Do not estimate token limits silently unless a provider tokenizer/capability supports it. Over-limit inputs should fail with a classified error or be pre-chunked before provider invocation.

## 7. Vector index provisioner

Add a database-backed boundary:

```csharp
IVectorIndexProvisioner
- EnsureBuildIndexAsync(profile)
- IsIndexReadyAsync(profile)
- DropRetiredIndexAsync(profile) // explicit/manual policy, not automatic during activation
```

For Phase 3.6B-Foundation:

- storage type: pgvector `vector`
- ANN index: HNSW
- metric: cosine
- maximum serving dimensions: 2,000

Index names must be deterministic and collision-safe, for example:

```text
IX_kce_hnsw_<profile-id-prefix>_v<indexVersion>
```

The SQL must use a profile-scoped partial HNSW expression index:

```sql
USING hnsw (("Embedding"::vector(<dimensions>)) vector_cosine_ops)
WHERE "EmbeddingProfileId" = '<profile-id>'
```

Never create SQL identifiers directly from untrusted user input.

## 8. Blue/green build flow

```text
Active Profile A continues serving
        |
        v
Create Building Profile B
        |
        v
Provision B HNSW
        |
        v
Reindex all target chunks/documents using B
        |
        v
Verify B coverage/completeness
        |
        v
Run deterministic evaluation
        |
        v
Run required offline/provider evaluation
        |
        v
Activation preflight
        |
      PASS
        |
        v
DB transaction:
  A Active -> Retired
  B Building -> Active
        |
        v
Commit
        |
        v
B serves new requests
```

If the activation transaction fails, A remains Active.

Do not delete A vectors/index during activation.

## 9. Build completeness

A Building profile cannot activate based only on profile status/index existence.

Activation preflight must prove expected vector coverage for the target corpus.

Minimum Phase 3.6B-Foundation invariant:

```text
Ready knowledge chunks count
==
Embedding rows for Building profile count
```

within the deployment scope being promoted.

If future product semantics allow intentionally excluded documents/chunks, the completeness contract must become explicit and versioned.

## 10. Activation service

Add an application service conceptually equivalent to:

```text
EmbeddingProfileActivationService.ActivateAsync(buildProfileId, evaluationEvidence)
```

Preconditions:

1. candidate status = Building;
2. exactly one current Active profile exists;
3. candidate provider/profile metadata is valid;
4. profile-specific HNSW exists and matches dimensions;
5. embedding coverage is complete;
6. deterministic evaluation thresholds pass;
7. any required offline/provider evaluation evidence is present;
8. no tenant-leakage result;
9. activation occurs inside one database transaction.

Transaction:

```text
oldActive.Retire()
candidate.Activate(now)
SaveChanges
Commit
```

The existing unique partial Active index is the final concurrency guard.

## 11. Evaluation evidence model

Do not make activation accept an arbitrary boolean `evaluationPassed=true`.

Use a structured result:

```text
RagEvaluationEvidence
- DatasetVersion
- EmbeddingProfileId
- Provider
- Model
- Dimensions
- IndexVersion
- HitRateAtK
- MeanRecallAtK
- MeanPrecisionAtK
- CitationCorrectness
- TenantLeakageCount
- CompletedAtUtc
- EvaluationKind: Deterministic | OfflineSemantic
- RunnerVersion
```

The Foundation gate can persist deterministic evidence.

Provider promotion must additionally reference the required offline semantic evidence or an explicit policy that it is not required.

## 12. Benchmark harness

Benchmarking is not the serving code path.

A benchmark runner should:

- load the versioned synthetic/non-private evaluation dataset;
- create/build a candidate profile;
- embed/index with that candidate;
- query with the same profile;
- compute deterministic metrics;
- capture latency and provider errors;
- estimate/report provider usage/cost using measured input counts plus configured price metadata;
- never automatically activate the candidate.

Benchmark output must be addressable by dataset/profile/run ID.

## 13. Provider adapters

### First structural adapter

Implement OpenAI adapter first behind mocks because:

- 1536-dimensional `text-embedding-3-small` fits the current HNSW `vector` limit;
- the adapter must support an explicit dimensions field for third-generation embeddings;
- API credentials remain server-side only.

This is an implementation order choice, not a provider winner.

### Second benchmark adapter

Voyage `voyage-4` at 1024 is the preferred second retrieval-specialized benchmark candidate if the live benchmark is authorized.

### Optional third

Cohere `embed-v4.0` at 1024/1536 may be added only if account/pricing/deployment requirements justify the additional adapter.

Do not implement three providers merely to increase code volume.

## 14. Secrets and live calls

No provider API key may appear in:

- source;
- migration;
- appsettings committed to Git;
- frontend;
- logs;
- evaluation output;
- telemetry.

Use standard server environment/secret configuration.

Mock/provider-contract tests must pass without credentials.

Live benchmark tests are opt-in and skipped/disabled without the required environment variable.

A live provider call is never part of ordinary deterministic unit tests.

## 15. Degraded behavior

If the Active provider is unavailable:

- do not query Building/Retired vectors;
- do not silently switch dimensions/models;
- expose degraded/unavailable status;
- optionally use a separately configured lexical-only fallback if the product policy enables it;
- record fallback/degraded mode in telemetry.

A provider outage must not mutate profile status automatically.

## 16. Observability

Every indexing/retrieval/benchmark run should record:

- profile ID/key;
- provider/model;
- dimensions;
- index version;
- purpose Query/Document;
- batch size;
- provider latency;
- vector-store latency;
- candidate count/final K;
- fallback/degraded mode;
- evaluation dataset/run version.

Do not log raw document contents or secrets.

## 17. Phase 3.6B-Foundation implementation packets

### Packet B1 — profile repository/resolvers
Owner: Claude Code
Review: GPT-5.6 Sol

### Packet B2 — provider contract/capabilities/purpose
Owner: Claude Code
Review: GPT-5.6 Sol

### Packet B3 — vector index provisioner + completeness checks
Owner: Claude Code
Validation: Desktop Commander on real PostgreSQL

### Packet B4 — activation service + evaluation evidence
Owner: Claude Code
Validation: transaction/concurrency/failure tests

### Packet B5 — OpenAI adapter contract implementation
Owner: Claude Code
Validation: mock HTTP tests only; no credential required

### Packet B6 — benchmark runner
Owner: Claude Code
Research/eval review: ChatGPT Work

### Packet B7 — live provider benchmark
Owner: Desktop Commander execution after explicit credential/cost authorization
Review: GPT-5.6 Sol + ChatGPT Work

## 18. Foundation merge gates

Before 3.6B-Foundation may be committed/pushed:

- active resolver cannot return Building/Retired/Failed profiles;
- build resolver cannot return Active/Retired profiles;
- retrieval always uses Active profile;
- index build/reindex uses Building profile;
- provider gets Query/Document purpose explicitly;
- batch size respects provider capability;
- >2,000 dimensions rejected for current HNSW vector serving path;
- profile-specific HNSW provisioning verified on PostgreSQL;
- build completeness verified;
- activation rolls back cleanly on any failed precondition;
- old Active remains Active on failure;
- successful activation swaps states atomically;
- concurrent activation cannot produce two Active profiles;
- deterministic evaluation evidence is profile/dataset/version bound;
- no provider key required for unit/integration test suite;
- Phase 3.6A migration/tenant tests remain green;
- full frontend/AI/build/migration/Docker gate remains green.

## 19. Explicit non-goals

Deferred unless evaluation demonstrates need:

- halfvec serving indexes;
- >2,000-dimensional float32 HNSW profiles;
- per-workspace Active profiles;
- multiple concurrent Building profiles;
- semantic reranking;
- automatic provider failover across incompatible vector spaces;
- automatic deletion of retired vectors/indexes;
- arbitrary user-configured providers from the browser.
