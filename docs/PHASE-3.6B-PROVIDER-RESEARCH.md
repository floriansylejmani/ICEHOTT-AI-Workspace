# Phase 3.6B — Production Semantic Provider Research

Status: research baseline; no provider activated
Branch: `phase-3.6b-provider-evaluation`
Base: `d75d08d`
Checked: 2026-09-24

## Executive summary

ICEHOTT Phase 3.6A now supports versioned embedding profiles and profile-scoped HNSW indexes. Phase 3.6B should not select a provider based on marketing benchmarks alone. The provider must pass the ICEHOTT versioned retrieval/citation evaluation dataset and operational gates.

The current architecture creates one decisive technical constraint:

- pgvector `vector` can store up to 16,000 dimensions;
- pgvector HNSW/IVFFlat indexes using the `vector` type support up to 2,000 dimensions.

Therefore any production profile using the current float32 `vector` + HNSW serving path must use at most 2,000 dimensions.

This rules out storing an OpenAI 3072-dimensional `text-embedding-3-large` vector unchanged in the current HNSW path. OpenAI supports shortening via the `dimensions` parameter, so a lower dimension such as 1024 can be evaluated instead. Voyage 4 supports 256/512/1024/2048; 1024 is directly compatible with the current HNSW `vector` path, while 2048 is above pgvector's 2,000-dimensional HNSW limit for float32 `vector`. Cohere Embed v4 supports 256/512/1024/1536, all of which are compatible with the current HNSW vector limit.

## Authoritative sources checked

### OpenAI

- Vector embeddings guide:
  https://developers.openai.com/api/docs/guides/embeddings
- text-embedding-3-large model:
  https://developers.openai.com/api/docs/models/text-embedding-3-large
- text-embedding-3-small model:
  https://developers.openai.com/api/docs/models/text-embedding-3-small
- API data controls:
  https://developers.openai.com/api/docs/guides/your-data

### Voyage AI

- Text embeddings:
  https://docs.voyageai.com/docs/embeddings
- Embeddings API:
  https://docs.voyageai.com/reference/embeddings-api
- Pricing:
  https://docs.voyageai.com/docs/pricing
- FAQ / retention:
  https://docs.voyageai.com/docs/faq
- Batch inference:
  https://docs.voyageai.com/docs/batch-inference

### Cohere

- Embed v4:
  https://docs.cohere.com/docs/cohere-embed
- Embed API:
  https://docs.cohere.com/reference/embed
- Rerank:
  https://docs.cohere.com/docs/rerank
- Rate limits:
  https://docs.cohere.com/v1/docs/rate-limits
- Pricing:
  https://cohere.com/pricing

### pgvector

- Official README:
  https://github.com/pgvector/pgvector/blob/master/README.md
- HNSW source limit:
  https://github.com/pgvector/pgvector/blob/master/src/hnsw.h

## Provider comparison

| Dimension | OpenAI | Voyage 4 | Cohere Embed v4 |
|---|---|---|---|
| Production model candidate | text-embedding-3-small / text-embedding-3-large | voyage-4 / voyage-4-large / voyage-4-lite | embed-v4.0 |
| Default dimensions | 1536 small / 3072 large | 1024 | 1536 |
| Configurable dimensions | supported through dimensions parameter | 256 / 512 / 1024 / 2048 | 256 / 512 / 1024 / 1536 |
| Safe with current pgvector HNSW vector path | small default 1536 yes; large must be shortened <=2000 | 256/512/1024 yes; 2048 no with float32 HNSW vector | all documented dimensions yes |
| Context | 8192 tokens for embedding v3 models | 32,000 tokens for Voyage 4 family | 128k for Embed v4 |
| Retrieval query/document mode | generic embeddings; application controls usage | explicit query/document input_type recommended | explicit search_query/search_document input_type required/recommended |
| Dedicated reranker from same provider | no dedicated embedding-family reranker in this research packet | rerank family available | rerank-v4.0-fast / rerank-v4.0-pro |
| Data controls | API content not used for training by default; embeddings eligible for ZDR; default abuse logs up to 30 days; regional support includes Europe for embeddings | hosted API customers can opt out for zero-day retention with account prerequisites | strong private/deployment options; encrypted Model Vault offers ZDR, but standard API contractual controls must be verified for the intended account |
| Batch/offline indexing | API batching supported; Batch API exists but has separate application-state considerations | Batch inference supports embeddings/rerank and up to 100K inputs per batch | EmbedJob / normal embed endpoints available |
| Public price signal checked | small $0.02 / 1M tokens; large $0.13 / 1M tokens | voyage-4-lite $0.02 / 1M; voyage-4 $0.06 / 1M; voyage-4-large $0.12 / 1M; first 200M tokens currently listed free for Voyage 4 family | production API is pay-as-you-go, but the current public pricing page emphasizes enterprise/Model Vault; exact self-serve Embed v4 token price should be verified in the account/pricing surface before promotion |

## OpenAI findings

### Dimensions and pgvector fit

OpenAI documents default dimensions of:

- `text-embedding-3-small`: 1536
- `text-embedding-3-large`: 3072

The API supports a `dimensions` parameter to shorten third-generation embeddings.

Recommended ICEHOTT evaluation profiles:

```text
openai-small-1536-v1
provider = openai
model = text-embedding-3-small
dimensions = 1536
distance = cosine
normalization = unit
```

and optionally:

```text
openai-large-1024-v1
provider = openai
model = text-embedding-3-large
dimensions = 1024
distance = cosine
normalization = unit
```

Do not use the 3072 default with the current float32 HNSW vector path.

### Cost

Current published embedding input prices checked on 2026-09-24:

- text-embedding-3-small: $0.02 / 1M tokens
- text-embedding-3-large: $0.13 / 1M tokens

### Privacy

OpenAI states API data is not used to train models by default unless the customer explicitly opts in.

For `/v1/embeddings`:

- application-state retention: none;
- default abuse-monitoring retention: up to 30 days;
- eligible for Zero Data Retention for approved organizations;
- data residency is available for embeddings in supported regions including Europe.

Production deployment must use an organization/project policy that matches ICEHOTT customer requirements; code must not assume ZDR is enabled merely because the endpoint supports it.

## Voyage findings

### Dimensions and context

Voyage 4-family:

- context length: 32k tokens;
- default dimension: 1024;
- supported dimensions: 256, 512, 1024, 2048.

For ICEHOTT's current HNSW `vector` path, use 1024 or lower. A 2048 profile would require a different index strategy such as halfvec, dimensionality reduction, or no HNSW float32 index.

Voyage recommends explicit `input_type=query` and `input_type=document` for retrieval. ICEHOTT should represent query-vs-document embedding purpose in the provider request contract before adding a Voyage adapter.

### Shared embedding space

Voyage states the Voyage 4 family uses a shared embedding space, allowing compatible embeddings across Voyage 4 models. This is potentially useful for asymmetric indexing/query cost strategies, but ICEHOTT must still encode the compatibility family in profile metadata rather than assuming models are interchangeable by provider name alone.

### Price

Current published pricing checked on 2026-09-24:

- voyage-4-lite: $0.02 / 1M tokens
- voyage-4: $0.06 / 1M tokens
- voyage-4-large: $0.12 / 1M tokens

The current Voyage pricing page lists the first 200M tokens free for Voyage 4-family text embedding models.

### Retention

Voyage documents that hosted API customers can opt out of storage/use for future training with zero-day retention, subject to account/payment/admin prerequisites.

ICEHOTT should treat this as an account configuration requirement, not an automatic property of the API.

## Cohere findings

### Dimensions and context

Cohere `embed-v4.0`:

- dimensions: 256 / 512 / 1024 / 1536;
- default: 1536;
- context: 128k tokens;
- supports text, images, and mixed inputs;
- retrieval distinguishes `search_document` and `search_query`.

All documented float dimensions fit under pgvector's 2,000-dimension HNSW limit.

### Request limits

The current Embed v2 reference lists a maximum of 96 text inputs/inputs per request. ICEHOTT's provider adapter must not reuse the local baseline batch size blindly; batch size belongs in provider capabilities/config.

### Reranking

Current Cohere rerank choices include:

- `rerank-v4.0-fast`
- `rerank-v4.0-pro`

Cohere production rate-limit documentation lists Rerank at up to 1,000 requests/minute for production keys and Embed at 2,000 inputs/minute, subject to account/model rules.

### Deployment/privacy

Cohere offers private/dedicated deployment options. Its encrypted Model Vault documentation describes ZDR and confidential-computing guarantees, but those guarantees must not be generalized to ordinary hosted API usage without verifying the applicable plan/contract.

## pgvector implications

Official pgvector documentation currently states:

- `vector` storage: up to 16,000 dimensions;
- HNSW / IVFFlat with `vector`: up to 2,000 dimensions;
- `halfvec` HNSW: up to 4,000 dimensions.

ICEHOTT Phase 3.6B should therefore add a second dimension concept:

```text
StorageDimensions
ServingIndexDimensions
```

only if the product intends to support profiles above 2,000 float32 dimensions.

For the first production semantic provider, the simplest and safest rule is:

```text
Dimensions <= 2000
StorageType = vector
IndexType = HNSW
Distance = cosine
```

This avoids introducing halfvec before evaluation proves it is needed.

## Recommended evaluation shortlist

Do not implement all providers simultaneously.

Evaluate these profiles first:

### Candidate A — cost/operational baseline

```text
OpenAI text-embedding-3-small
1536 dimensions
cosine
```

Why include it:
- fits current HNSW;
- low published token cost;
- mature API/data-control story;
- no dimensionality reduction required.

### Candidate B — retrieval-specialized baseline

```text
Voyage voyage-4
1024 dimensions
query/document input_type
cosine
```

Why include it:
- retrieval-focused API;
- 32k context;
- 1024 default fits current HNSW;
- paired Voyage reranker option;
- published cost between OpenAI small and large.

### Candidate C — enterprise/rerank baseline

```text
Cohere embed-v4.0
1024 or 1536 dimensions
search_query/search_document
cosine
```

Why include it:
- long context;
- both candidate dimensions fit current HNSW;
- mature dedicated rerank family;
- enterprise/private deployment options.

Candidate C can remain optional until API pricing/account access is confirmed.

## No-winner policy

No provider is selected by this research memo.

The winner must be the first candidate that meets all required operational constraints and has the best measured tradeoff on the expanded ICEHOTT evaluation set.

Provider benchmark results must record:

- provider/model;
- dimension;
- index version;
- dataset version;
- document embedding time;
- query embedding p50/p95;
- retrieval p50/p95;
- Recall@1/3/5;
- Precision@1/3/5;
- citation correctness;
- forbidden-source leakage;
- tenant leakage;
- estimated indexing cost;
- estimated query cost;
- provider error/fallback rate.

## Required Phase 3.6B architecture changes before live provider activation

1. Separate `ServingEmbeddingProfileResolver` from `BuildEmbeddingProfileResolver`.
2. Extend provider request semantics with embedding purpose:
   - Document
   - Query
3. Move provider batch size and max input constraints into provider capabilities.
4. Add profile-specific HNSW index creation/provisioning.
5. Add a transactional activation service:
   - verify Building profile complete;
   - verify compatible HNSW exists;
   - verify deterministic gates;
   - verify required offline eval result;
   - retire old Active;
   - activate new profile.
6. Keep retrieval on Active until activation transaction commits.
7. Persist or export benchmark/evaluation result metadata.
8. Add explicit degraded/fallback status; never silently query vectors from a different profile.
9. Keep API keys only in server/deployment secret configuration.

## Proposed implementation order

### 3.6B-1 — Active vs Building resolver
No external credentials required.

### 3.6B-2 — provider request purpose/capabilities
No external credentials required.

### 3.6B-3 — OpenAI adapter behind IEmbeddingProvider
Implement and mock-test without a real key.
Recommended first profile for structural validation: `text-embedding-3-small` at 1536.

### 3.6B-4 — Voyage adapter or benchmark harness
Implement only if the benchmark will actually be run.
Recommended profile: `voyage-4` at 1024.

### 3.6B-5 — index provisioner + activation transaction
Required before either provider can become Active.

### 3.6B-6 — expanded evaluation set
Increase beyond the current four synthetic cases before semantic promotion.

### 3.6B-7 — live benchmark
Requires user-authorized provider credentials and incurs provider usage/cost according to the account.

### 3.6B-8 — semantic reranker
Only after baseline embedding comparison shows retrieval quality/candidate ordering needs it.

## Decision gate

Phase 3.6B must not merge a provider as Active until:

- dimensions <= serving-index capability;
- real pgvector index build passes;
- full reindex completes;
- old Active remains available until promotion;
- deterministic gates pass;
- offline semantic evaluation passes;
- latency/cost are recorded;
- provider privacy configuration is reviewed;
- provider outage/fallback behavior is proven;
- no tenant leakage is observed.
