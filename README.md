# ICEHOTT AI Workspace

![CI](https://github.com/floriansylejmani/ICEHOTT-AI-Workspace/actions/workflows/ci.yml/badge.svg)

ICEHOTT is a production-oriented AI workspace for agentic work: knowledge, tools, workflows, approvals, artifacts, and observability in one product.

## Current status

### Phase 0 — Foundation
- Next.js 16 frontend
- ASP.NET Core 9 API with Clean Architecture boundaries
- Python FastAPI AI service
- PostgreSQL + pgvector
- Redis
- Docker Compose
- GitHub Actions CI
- Health/readiness endpoints
- Architecture, security, PRD, and AI-evaluation baselines

### Phase 1 — Identity & Workspaces
- Register / login / logout / current-user flow
- ASP.NET Core password hashing
- Short-lived JWT access tokens kept in frontend memory
- HttpOnly refresh-token cookie with server-side SHA-256 hash storage
- Refresh-token rotation and replay rejection
- CSRF header requirement for refresh/logout cookie actions
- Authentication rate limiting
- Workspace creation and membership
- Owner / Admin / Member roles
- Server-side tenant isolation
- Server-side admin settings gate
- Next.js login, registration, and workspace shell
- EF Core PostgreSQL migration
- API and frontend integration tests
- CI migration validation against a real pgvector/PostgreSQL service

### Phase 2 — AI Agent Runtime
- Authenticated workspace-scoped AI chat
- Conversation and message history persisted in PostgreSQL
- ASP.NET Core → FastAPI runtime boundary
- Tenant isolation enforced before conversation access
- Active `Ask ICEHOTT…` dashboard input
- Latest conversation restoration and new-chat flow
- Phase 2 EF Core migration
- Backend, frontend, and AI runtime tests
- Docker AI health dependency and runtime verification

### Phase 3 — Knowledge / RAG
- Workspace-scoped knowledge ingestion and deletion
- Server-side PDF / DOCX / TXT / MD / CSV / JSON extraction
- 10 MB upload limit, filename sanitization, and extension allowlist
- Deterministic overlapping chunking with batched embeddings
- PostgreSQL pgvector `vector(64)`
- Hybrid semantic + lexical retrieval
- HNSW cosine index plus GIN full-text index
- Embedding rows protected by chunk/workspace ownership checks and workspace FK integrity
- Composite database tenant constraints across conversations, messages, citations, documents, chunks, and embeddings
- RAG context injected into agent chat
- Server-derived source citations persisted with messages
- Knowledge upload/search/delete UI in the dashboard
- Tenant-isolated retrieval and upload integration tests

### Phase 3.5 — Production RAG Hardening
- Durable asynchronous ingestion jobs in PostgreSQL
- Atomic multi-worker leasing with `FOR UPDATE SKIP LOCKED`
- Lease heartbeat, expired-lease recovery, bounded retry/backoff, and max-attempt failure
- Idempotent reindexing and stale-worker ownership protection
- Structure-aware chunking with overlap
- Provider-neutral embedding and retrieval interfaces
- Expanded candidate retrieval plus deterministic hybrid reranking and document diversification
- Retrieved-content prompt-injection / unsafe-tool policy
- RAG ActivitySource + Meter instrumentation
- Database and AI-runtime readiness checks
- Queued/Processing polling and reindex UX in the dashboard

### Phase 3.6A — Semantic RAG Foundation
- Versioned embedding profiles with explicit lifecycle and dimensions
- Profile-aware pgvector storage and retrieval
- Existing 64-dimensional vectors preserved through profile backfill
- Profile-scoped HNSW serving index
- Single Active profile database invariant
- Readiness checks for profile metadata plus compatible HNSW index
- Versioned deterministic RAG evaluation dataset and metrics
- Production semantic provider intentionally deferred to Phase 3.6B

### Phase 3.6B — Semantic Provider Foundation
- Separate Active serving profile and Building profile paths
- Provider registry with explicit Query/Document purpose and provider capabilities
- Stable-chunk blue/green vector build without replacing serving chunk IDs
- Single-workspace external provider batching
- Profile-scoped HNSW index provisioning and coverage checks
- Structured deterministic/offline evaluation evidence
- Tenant-leakage-aware benchmark runner and explicit dataset thresholds
- PostgreSQL SERIALIZABLE activation with final coverage/index/evidence rechecks
- Mock-tested OpenAI `text-embedding-3-small` 1536-dimensional adapter
- Provider-configuration-aware readiness
- Real PostgreSQL/pgvector activation rollback/swap integration tests
- `rag-v2` expanded evaluation corpus with SHA-256 freeze and tenant-isolation fixture
- Opt-in live provider benchmark CLI with fresh-database, secret, pricing, and cost-cap guards
- Bounded offline semantic judge with structured evidence and no external evaluation of safety-only cases
- CI benchmark dry-run verifies the frozen corpus without provider cost
- Authorized `rag-v2` live benchmark PASS for OpenAI `text-embedding-3-small`: deterministic metrics 1.00, offline semantic metrics 1.00, tenant leakage 0, promotion policy PASS
- Measured full-gate provider cost $0.00100536 under the approved $0.05 cap
- Candidate activation remains a separate reviewed operation and is never performed automatically by benchmark code

### Phase 4 — Agent Tools & Execution
- Server-authoritative workspace tool registry with typed argument schemas
- Workspace role checks before every tool request, read, approval, rejection, or execution
- `ReadOnly` and `SensitiveWrite` risk classes
- Sensitive writes persist as `PendingApproval` and require a different Admin/Owner approver
- Requester permission is revalidated at approval time so revoked/demoted requests cannot execute
- Execution reads are role-filtered so Member users cannot inspect Admin-only tool arguments/results
- Canonical JSON argument hashing plus workspace/tool-scoped idempotency keys
- Persisted execution state machine and result JSON
- Append-only application audit events for Requested/Approved/Rejected/Started/Succeeded/Failed transitions
- Composite workspace/execution foreign keys protecting tenant-owned audit data
- Built-in `workspace.echo` safe tool and approval-gated `workspace.audit-note.create` write tool
- Phase 4 EF migration validated from zero on PostgreSQL/pgvector
- Tenant crossing, self-approval, under-privileged approval, invalid-schema, and idempotency tests

### Phase 4.5 — Security, Approvals & Audit Hardening
- Versioned per-workspace tool policy overlays and approval-time policy revalidation
- Transactional per-workspace/per-tool quotas with idempotent retry semantics
- Bounded request bodies, credential-pattern rejection, and secret-safe result/error handling
- Execution deadlines, cancellation, leases, and `OutcomeUnknown` recovery without automatic write replay
- Approval commit protected against concurrent requester/approver membership removal or demotion
- PostgreSQL-enforced append-only execution and policy audit rows
- Prompt-injection abuse tests proving untrusted text cannot grant approval or tool authority

### Phase 5A — Workflows & Artifacts Architecture Freeze
- Durable workflow state machines, version pinning, lease fencing, run-as revalidation, checkpoints, artifacts, and bounded scheduling contracts
- PostgreSQL remains the durable orchestration authority; Redis is optional coordination only
- Sensitive tool work preserves all Phase 4.5 policy, approval, idempotency, quota, secret, and audit guarantees

### Phase 5B — Domain & Persistence
- Workflow definitions/versions/runs/steps/checkpoints/triggers, artifact metadata, and append-only workflow audit domain model
- Workspace-aware composite foreign keys, Active-version uniqueness, run idempotency, trigger-fire dedupe, and lease-generation persistence
- Repository layer with explicit workspace-scoped reads
- PostgreSQL migration validated from clean DB, Phase 4.5 upgrade, rollback/reapply, and idempotent script replay
- Full backend Debug and Release suites: 406/406 passing with PostgreSQL integration tests enabled

### Phase 5C — Durable Runner & Recovery
- PostgreSQL run queue with `FOR UPDATE SKIP LOCKED`, lease expiry, heartbeat, and monotonic fencing generations
- Durable delay/retry/cancellation state plus restart-safe reclaim
- Run-as membership revalidation before each new step
- Stable workflow tool idempotency and Phase 4.5-preserving child-scope tool invocation
- `OutcomeUnknown` propagation without blind replay of uncertain sensitive side effects
- Hosted workflow runner with real restart-recovery smoke evidence
- Full backend Debug and Release suites: 426/426 passing with PostgreSQL integration tests enabled

**Next:** Phase 5D — Artifacts & File Storage.

## Architecture

```text
Browser / Next.js
      |
      | HTTPS + access token
      v
ASP.NET Core API
      |
      +---- PostgreSQL + pgvector
      |
      +---- Redis
      |
      v
Python AI service
      |
      v
Model / tool runtime
```

The AI runtime is intentionally separate from identity and core product authorization. Phase 4 tool execution remains server-authoritative in ASP.NET Core: model output can never bypass workspace membership, schema validation, idempotency, or human approval.

## Local services

- Web: http://localhost:3000
- API: http://localhost:5050
- AI service: http://localhost:8000
- PostgreSQL: localhost:5432
- Redis: localhost:6379

## Local setup

```bash
docker compose up -d postgres redis
dotnet tool restore
dotnet ef database update --project backend/src/ICEHOTT.Persistence/ICEHOTT.Persistence.csproj --startup-project backend/src/ICEHOTT.API/ICEHOTT.API.csproj
```

Run the API:

```bash
dotnet run --project backend/src/ICEHOTT.API/ICEHOTT.API.csproj
```

Run the web app:

```bash
cd apps/web
npm ci
npm run dev
```

Run the AI service:

```bash
cd ai/icehott-ai-service
python -m uvicorn app.main:app --reload
```

Copy `.env.example` to your local environment and replace development placeholders. Never commit real credentials.

## Validation

The merge gate requires:
- .NET build with zero errors
- backend tests
- frontend lint
- frontend tests
- Next.js production build
- AI tests
- EF migration script validation
- migration application to pgvector/PostgreSQL in GitHub Actions

See [Phase 1](docs/PHASE-1-IDENTITY-WORKSPACES.md), [Phase 2](docs/PHASE-2-AGENT-RUNTIME.md), [Phase 3](docs/PHASE-3-KNOWLEDGE-RAG.md), [Phase 3.5](docs/PHASE-3.5-PRODUCTION-RAG-HARDENING.md), [Phase 3.6 architecture freeze](docs/PHASE-3.6-ARCHITECTURE-FREEZE.md), [Phase 3.6A semantic foundation](docs/PHASE-3.6A-SEMANTIC-FOUNDATION.md), [Phase 3.6B provider research](docs/PHASE-3.6B-PROVIDER-RESEARCH.md), [Phase 3.6B provider architecture](docs/PHASE-3.6B-ARCHITECTURE.md), [Phase 3.6B provider foundation](docs/PHASE-3.6B-PROVIDER-FOUNDATION.md), [Phase 4 agent tools architecture](docs/PHASE-4-AGENT-TOOLS-ARCHITECTURE.md), [Multi-agent engineering roadmap](docs/MULTI-AGENT-ENGINEERING-ROADMAP.md), [API contract](docs/API.md), [Architecture](docs/ARCHITECTURE.md), and [Security](docs/SECURITY.md).
