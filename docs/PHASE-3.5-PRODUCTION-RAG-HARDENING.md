# Phase 3.5 — Production RAG Hardening

## Goal

Phase 3.5 hardens the Phase 3 Knowledge/RAG pipeline for production-style failure modes, concurrency, retries, retrieval safety, and operability without moving tenant authorization or persistence ownership out of ASP.NET Core.

## Delivered

- Asynchronous knowledge ingestion with durable PostgreSQL processing jobs
- Atomic PostgreSQL job leasing with `FOR UPDATE SKIP LOCKED`
- Worker ownership checks for renew, complete, retry, and fail transitions
- Lease heartbeat and expired-lease recovery
- Bounded exponential retry backoff and maximum-attempt failure state
- Idempotent document reindexing
- Structure-aware chunking with overlap and fenced-code preservation
- Embedding provider abstraction through `IEmbeddingProvider`
- Retrieval abstraction through `IKnowledgeRetriever`
- Candidate expansion followed by deterministic hybrid reranking
- Per-document diversification in final RAG context
- Retrieved-content policy for common prompt-injection and unsafe-tool instruction patterns
- RAG ActivitySource and Meter instrumentation
- API readiness now verifies PostgreSQL and the AI runtime
- Frontend polling for Queued/Processing documents and explicit reindex UX
- Database indexes for queued work and expired lease recovery

## Ingestion lifecycle

```text
Upload / pasted text
       |
       v
membership + validation
       |
       v
KnowledgeDocument = Queued
       |
       +--> knowledge_processing_jobs
                 |
                 v
         worker leases job
         (SKIP LOCKED)
                 |
                 v
          document Processing
                 |
                 v
      structure-aware chunking
                 |
                 v
        embedding provider
                 |
                 v
       PostgreSQL + pgvector
                 |
                 v
          document Ready
                 |
                 v
          job Completed
```

If processing fails, the worker releases the lease and schedules a bounded exponential retry. After the configured maximum attempts, the job and document transition to `Failed`.

## Queue safety

A processing job stores:

- `Status`
- `Attempts` / `MaxAttempts`
- `AvailableAtUtc`
- `LockedBy`
- `LockedUntilUtc`
- `LastError`
- timestamps

Only the worker that owns `LockedBy` can renew, complete, retry, or fail a lease. An expired Processing lease is eligible for recovery by another worker. PostgreSQL leasing uses one atomic `UPDATE ... FROM (SELECT ... FOR UPDATE SKIP LOCKED)` statement so multiple API replicas can run workers without leasing the same job.

## Reindex semantics

Reindex is idempotent:

- `Queued` or `Processing`: returns the current document state and does not create duplicate work.
- `Ready` or `Failed`: resets the durable job and queues a new indexing attempt.

The job table enforces one processing job per `(DocumentId, WorkspaceId)`.

## Chunking

`StructureAwareKnowledgeChunker` targets approximately 450 words with an 80-word overlap. It preserves paragraph/block boundaries where practical and keeps fenced code blocks together until a block must be split for size.

This is deterministic chunking. It is structure-aware, not an LLM semantic segmenter.

## Retrieval hardening

Retrieval now follows:

```text
query
  |
  v
embedding
  |
  v
hybrid pgvector + lexical candidates
  |
  v
score floor
  |
  v
retrieved-content safety policy
  |
  v
hybrid reranker
  |
  v
document diversification
  |
  v
final top-K context
```

The retriever expands the candidate pool before reranking. The reranker combines the database score with query-token coverage and exact phrase coverage, then limits dominance by a single document before filling remaining slots.

## Retrieved-content safety

Retrieved chunks are treated as untrusted data. The current deterministic policy rejects common patterns for:

- overriding prior/system/developer instructions
- exfiltrating system/developer prompts
- requesting unsafe tool execution while bypassing approval

This is a defense-in-depth filter, not a complete prompt-injection solution. Future model/tool phases must continue to keep retrieved content below trusted system/developer instructions and validate all executable tool arguments server-side.

## Embedding provider boundary

`IEmbeddingProvider` separates indexing/retrieval from the current FastAPI embedding implementation. Phase 3.5 keeps the deterministic 64-dimensional local embedding runtime as the default development baseline; it does not claim production semantic quality.

A production semantic embedding provider can replace `AiRuntimeEmbeddingProvider` without changing tenant authorization, job orchestration, retrieval interfaces, pgvector ownership, or frontend contracts.

## Observability

`ICEHOTT.Rag` exposes ActivitySource/Meter signals for:

- retrieval requests
- filtered chunks
- retrieval latency
- result count
- indexing latency
- indexed chunk count
- indexing failures
- completed/retried/failed jobs

Deployment environments can attach an OpenTelemetry exporter without moving instrumentation into Domain code.

## Readiness

`GET /ready` reports ready only when both:

- PostgreSQL is reachable
- FastAPI AI runtime `/ready` succeeds

`GET /health` remains the lightweight API liveness endpoint.

## API behavior

Knowledge ingestion endpoints return `202 Accepted` with document status `Queued`.

Reindex:

`POST /api/workspaces/{workspaceId}/knowledge/documents/{documentId}/reindex`

The frontend polls document status while any document is `Queued` or `Processing`.

## Database

Phase 3.5 migrations:

- `20260924132408_Phase35ProductionRagHardening`: durable processing jobs and queue indexes
- `20260924142517_Phase35QueueLeaseIndex`: expired-processing lease lookup index

## Validation targets

The Phase 3.5 merge gate covers:

- asynchronous ingest -> queued -> processing -> ready
- large-document batched embeddings
- tenant-isolated retrieval and citations
- file upload/delete
- cross-workspace database integrity
- retrieved-content policy
- structure-aware chunk overlap
- reranker diversification
- processing-job lifecycle
- reindex idempotency
- stale-worker ownership rejection
- frontend queued/processing polling and reindex UX
- FastAPI tests
- migration generation/application on PostgreSQL
- Docker builds and live readiness checks

## Final validation — September 24, 2026

- Backend Debug: 26/26 tests passed
- Backend Release: 26/26 tests passed
- Frontend: 8/8 tests passed
- Frontend ESLint: passed
- Next.js production build: passed
- FastAPI: 5/5 tests passed
- EF Core idempotent migration script: generated successfully
- Docker API + AI images: built successfully
- Phase 3.5 migrations: applied successfully to PostgreSQL
- Queue indexes: verified in PostgreSQL
- API /health: 200
- API /ready: 200 with database and AI runtime ready
- AI /ready: 200
- Frontend /app: 200
- Real PostgreSQL worker smoke: Queued -> Processing -> Ready; job Completed on attempt 1; chunk and embedding persisted; smoke data removed afterward

## Remaining production upgrades

The following are intentionally not claimed as complete by Phase 3.5:

- malware scanning / deep file-signature validation
- a production semantic embedding/model provider
- model-based reranking
- versioned offline RAG quality datasets and promotion thresholds
- OpenTelemetry collector/exporter deployment configuration
- distributed object storage for original uploads

Those can be added without changing the Phase 3.5 application boundaries.
