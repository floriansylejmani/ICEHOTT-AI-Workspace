# Phase 7 — Production Deployment & SRE

**Status:** Architecture specification (approved)
**Baseline:** `main` at `c63a6b2337a1e17c056d5adbefc1daba83e8abf1`
**Previous phase:** Phase 6A — Workflow Authoring & Artifact-Gated Execution
**Architecture choice:** Approach B — Managed production architecture

Implementation status is tracked per subphase at the end of this document. Subphase 7A is documented in [PHASE-7A-PRODUCTION-ENVIRONMENT-RELEASE-FOUNDATION.md](PHASE-7A-PRODUCTION-ENVIRONMENT-RELEASE-FOUNDATION.md).

## 1. Objective

Move ICEHOTT from a production-oriented local/CI system into a deployable, observable, recoverable and securely operated production platform.

Phase 7 must preserve all existing identity, tenant isolation, RAG, tool execution, approval, workflow, scheduling and artifact guarantees while replacing development-only infrastructure assumptions with production-safe deployment contracts.

Phase 7 is considered complete only when staging and production paths are independently deployable, migrations are controlled, secrets are externalized, telemetry and alerts exist, backups are restore-tested and the production release procedure has executable evidence.

## 2. Architecture

### 2.1 Production topology

```
                    Internet
                       |
                       v
              +----------------+
              | Vercel / Web   |
              | Next.js 16     |
              +--------+-------+
                       |
                     HTTPS
                       |
                       v
              +----------------+
              | Public API     |
              | ASP.NET Core 9 |
              | Railway        |
              +---+--------+---+
                  |        |
       private    |        | private
       network    |        | network
                  v        v
          +-----------+  +-----------+
          | Worker    |  | FastAPI AI|
          | ASP.NET   |  | Service   |
          +-----+-----+  +-----+-----+
                |              |
                +------+-------+
                       |
          +------------+-------------+
          |            |             |
          v            v             v
     PostgreSQL      Redis        OTEL Collector
     + pgvector                     |
                                    v
                              Observability
                                Backend

                       |
                       v
              S3-compatible
              Object Storage
```

Only the web application and ASP.NET API receive public ingress. The AI service, worker processes, PostgreSQL, Redis and telemetry collector remain private services. The browser must never communicate directly with PostgreSQL, Redis, the AI service, worker service or object storage credentials.

## 3. Environment model

ICEHOTT has three explicitly different environments.

### Development

Used only for local development.

- Docker Compose remains supported.
- Local PostgreSQL + pgvector.
- Local Redis.
- Local deterministic embedding provider remains supported.
- Local filesystem artifact provider remains supported.
- Development JWT placeholders are permitted only in Development.
- Automatic migration may remain opt-in for local development.
- Development data must never be copied automatically to staging or production.

### Staging

Staging must be structurally equivalent to production. It must have independent:

- PostgreSQL database;
- pgvector extension/indexes;
- Redis instance;
- artifact bucket/prefix;
- JWT signing secret;
- OpenAI/provider credentials;
- API URL;
- CORS origin;
- telemetry environment identifier;
- deployment service configuration.

Staging must never share a database, Redis namespace, bucket or secret with production.

### Production

Production must fail closed when required configuration is missing or unsafe. Production must reject:

- empty JWT signing keys;
- known development/default JWT values;
- development database passwords;
- Development ASP.NET environment;
- local filesystem artifact storage unless explicitly allowed by a production safety override that is disabled by default;
- automatic schema migration on normal API startup;
- missing production object-storage configuration;
- insecure database configuration where transport security is required.

## 4. Deployment providers

### Web

Vercel is the reference deployment target for the Next.js application. The web deployment receives only public configuration such as API base URL, application environment identifier and safe feature flags. No database, Redis, object-store or server provider secrets may be exposed through `NEXT_PUBLIC_*`.

### API, worker and AI service

Railway is the reference container deployment platform. Production services:

- `icehott-api`
- `icehott-worker`
- `icehott-ai`
- `icehott-otel`

Internal service-to-service calls use private networking. Only `icehott-api` receives public ingress.

### PostgreSQL / pgvector

Production requires PostgreSQL 16 or newer with pgvector. The architecture must not depend on a specific database vendor API. Requirements:

- pgvector available;
- encrypted connection where applicable;
- dedicated database per environment;
- application user is not a superuser;
- migration credential can be separate from runtime credential;
- automated backups;
- restore procedure;
- connection limit known and enforced;
- HNSW indexes compatible with the active embedding profiles.

The initial Railway deployment may use its pgvector-capable PostgreSQL template, but persistence code remains standard PostgreSQL/pgvector.

### Redis

Redis remains optional for acceleration/coordination. PostgreSQL remains the durable authority for workflow runs, leases, approvals, artifact metadata, tool execution, schedules and ingestion jobs. Redis loss must not destroy durable workflow/application state.

### Artifact object storage

Production artifact bytes move behind an object-store abstraction. Required production contract:

- S3-compatible API;
- private bucket;
- server-side authenticated access;
- streamed upload/download;
- SHA-256 verification;
- bounded object size;
- object key validation;
- no public anonymous object access;
- idempotent finalization;
- durable delete;
- safe recovery after partial upload/failure.

The current filesystem provider remains the Development/test provider. Production provider selection is deployment configuration and must not affect application/domain interfaces.

## 5. API and worker separation

The existing ASP.NET Core image supports two production roles.

### API role

Enabled: HTTP API; authentication; workspace APIs; tools API; workflow mutation/read APIs; artifact APIs; health/readiness.

Disabled: knowledge background worker; workflow runner; workflow scheduler; artifact maintenance worker where safe to separate.

### Worker role

Uses the same application version/image. Enabled: KnowledgeWorker; WorkflowRunner; WorkflowScheduler; artifact maintenance/recovery. The worker has no required public ingress.

This separation prevents API scaling from accidentally multiplying background workloads while preserving the existing PostgreSQL leasing/fencing model.

## 6. Production configuration

Add explicit production configuration. Required configuration groups include `ConnectionStrings`, `Jwt`, `AiRuntime`, `OpenAiEmbedding`, `ArtifactStorage`, `ObjectStorage`, `KnowledgeWorker`, `WorkflowRunner`, `WorkflowScheduler`, `Cors`, `OpenTelemetry` and production safety options.

`appsettings.Production.json` contains safe non-secret defaults only. Secrets are supplied by the hosting platforms. Production startup must perform configuration validation before serving requests. Invalid configuration must terminate startup with a safe error that does not print secret values.

## 7. Secrets management

No production secret may be committed to Git. Secrets include at minimum: JWT signing material; PostgreSQL password/URL; Redis credentials; embedding/model provider keys; object-storage access keys; OTLP exporter authentication headers; email/provider keys introduced later.

Vercel secrets belong in Vercel environment configuration. Railway service secrets belong in Railway Variables/reference variables. GitHub Actions receives only deployment credentials required for deployment.

Secrets must never appear in: normal application logs; exceptions returned to the browser; workflow audit JSON; tool result JSON; telemetry attributes; test snapshots; generated artifacts.

## 8. Object storage abstraction

Introduce a provider boundary, for example `IArtifactObjectStore`.

Responsibilities: stage object; finalize object; open read stream; verify existence/metadata; delete object; clean stale staging object.

Implementations: `LocalArtifactObjectStore`, `S3ArtifactObjectStore`.

The domain/database continues to own artifact state and authorization. The object store owns bytes only. A successful object upload without the corresponding successful database transition must be recoverable by maintenance. A successful database transition without a durable object must fail closed and be detectable by reconciliation.

## 9. Database migrations

Production API startup must not automatically migrate the schema. Set `Database__AutoMigrate=false` in staging and production. Deployment uses an explicit migration stage. Recommended mechanism: `dotnet ef migrations bundle` or an equivalently version-pinned migration executable.

Release order:

```
build
  ↓
tests
  ↓
migration compatibility check
  ↓
deploy candidate to staging
  ↓
staging migration
  ↓
staging smoke
  ↓
production approval
  ↓
production migration
  ↓
production deploy
  ↓
production smoke
```

Migration failure stops the deployment. Production schema changes follow expand/contract rules whenever application rollback must remain possible. Automatic `database update` during ordinary application startup is forbidden in production. Automatic destructive database downgrade is forbidden.

## 10. CI/CD

Existing CI remains mandatory. Every pull request must run: backend build; backend tests; frontend lint; frontend tests; frontend production build; AI tests; EF migration validation; deterministic RAG evaluation checks; formatting/diff checks.

Phase 7 adds: production configuration validation tests; container build tests; object-storage integration tests; deployment manifest/config validation; OpenTelemetry configuration validation; backup/restore scripts validation; lightweight load smoke tests.

### Staging deployment

A green `main` commit may deploy automatically to staging. Deployment records the exact Git SHA. Staging must pass: API liveness; API readiness; AI readiness; database readiness; pgvector/profile readiness; authentication smoke; tenant-isolation smoke; artifact round-trip; workflow run smoke; scheduled worker smoke where practical.

### Production deployment

Production requires explicit promotion after staging passes. Production deploys the exact already-tested commit/image. No source rebuild with changed dependencies is allowed between staging approval and production promotion.

## 11. Container strategy

Build immutable production images: `icehott-api:<git-sha>` and `icehott-ai:<git-sha>`. The ASP.NET image may serve both API and worker roles through runtime configuration. Images must: run as non-root where practical; contain no `.env` secrets; use production runtime layers; expose only required ports; use deterministic dependency restore/build; have health probes compatible with the hosting platform.

Mutable `latest` alone is not a sufficient production release identifier. Git SHA is the canonical deployment version.

## 12. OpenTelemetry

ICEHOTT adopts OpenTelemetry as the vendor-neutral observability contract. Signals: traces; metrics; structured logs where supported. Application services send OTLP to `icehott-otel`. The Collector exports to the configured observability backend. Application code must not depend directly on one monitoring vendor.

Safe attributes: service name; environment; deployment Git SHA; route template; HTTP status class; workspace operation category; workflow state category; worker type; embedding provider/model/profile ID; duration; success/failure classification.

Do not emit: JWTs; passwords; provider API keys; refresh tokens; raw prompts by default; artifact contents; document contents; tool secrets; sensitive user data. High-cardinality IDs must be bounded or excluded from metrics labels.

## 13. Operational metrics

At minimum expose/collect:

- **API:** request count; request latency; 4xx count; 5xx count; active requests.
- **Database:** connection failures; query duration where instrumented; migration state/readiness; connection pool saturation.
- **Knowledge/RAG:** queue depth; oldest queued job age; indexing success/failure; embedding latency/error rate; retrieval latency; fallback rate.
- **Workflows:** queued run count; oldest queued/waiting duration; lease recovery count; workflow success/failure; `OutcomeUnknown` count; checkpoint wait duration; scheduler claim/fire failures.
- **Tools:** execution count; approval wait duration; failure count; quota rejection; timeout/cancellation; `OutcomeUnknown`.
- **Artifacts:** upload count; upload failures; storage bytes; pending artifact age; maintenance recovery/failure count.

## 14. Alerts

**Critical:** API readiness unavailable continuously for 2 minutes; production database unreachable; artifact object storage unavailable for write/read verification; migration failure during production promotion.

**High:** API 5xx rate > 2% for 5 minutes with sufficient request volume; oldest workflow queued item > 5 minutes unexpectedly; oldest knowledge ingestion job > 10 minutes unexpectedly; repeated workflow lease recovery; repeated `OutcomeUnknown` events; object-store reconciliation failures.

**Warning:** elevated API p95 latency; Redis unavailable while PostgreSQL remains healthy; storage quota nearing configured threshold; unusual worker retry rate.

Alerts must identify environment and service without exposing tenant-sensitive payloads.

## 15. SLOs

Phase 7 defines operational targets, not contractual customer SLAs.

- **Control-plane availability:** 99.9% monthly for the public web/API control plane, excluding explicitly declared maintenance and external model-provider failures outside ICEHOTT control.
- **Non-AI API performance gate:** 25 requests/second sustained for 10 minutes; p95 server response time <= 750 ms for selected non-AI endpoints; unexpected 5xx rate < 1%; no database connection-pool exhaustion; no tenant leakage. AI/model latency is measured separately because it depends on external providers.

## 16. Load testing

Use version-controlled load tests; recommended tool: k6. Test classes: authentication/read path; workspace listing; workflow run creation; idempotent concurrent run creation; artifact metadata reads; selected RAG retrieval endpoint; scheduler/worker pressure where practical.

PR CI runs a short smoke load. Full load tests run manually or on a scheduled performance workflow and do not block every small PR unless regression thresholds are exceeded in a stable environment. Load testing must never target production with destructive scenarios.

## 17. Backup strategy

Database backup is mandatory. Minimum initial target: **RPO <= 1 hour, RTO <= 2 hours**.

Production requires: provider/database automated backup capability; scheduled logical backup; encrypted backup storage; backup outside the live database volume; documented retention; automated verification that backup objects exist; periodic restore test.

Retention baseline: hourly 48 hours; daily 14 days; weekly 8 weeks. Secrets are not embedded in backup files.

## 18. Artifact backup/recovery

Artifact recovery must account for both PostgreSQL metadata and object bytes. Database and object-store restoration procedures must include reconciliation. After restore: artifact metadata pointing to missing objects is detected; orphan objects not represented by valid metadata are detected; no artifact is exposed solely because an object exists; authorization remains database/server-authoritative.

## 19. Disaster recovery

Create a production DR runbook covering: API container failure; AI service failure; worker failure; Redis loss; PostgreSQL loss/corruption; object storage outage; bad application release; failed schema migration; leaked/revoked provider credential; regional/provider outage.

For each incident specify: detection; user-visible impact; first response; fail-closed behavior; rollback/recovery procedure; verification; escalation condition. A restore drill must prove the documented recovery path.

## 20. Release rollback

Application rollback: redeploy the previously known-good immutable image; preserve current durable database state.

Schema rollback: not performed automatically; use backward-compatible migrations where possible; use forward-fix for already-committed data migrations unless an explicitly validated rollback exists.

Release metadata must record: Git SHA; migration version; deployment timestamp; environment.

## 21. Security invariants

Phase 7 must preserve:

1. Server-authoritative workspace identity.
2. Tenant isolation.
3. Current membership revalidation.
4. Phase 4.5 tool approval/security guarantees.
5. Workflow lease fencing.
6. Workflow idempotency.
7. Append-only audit trails.
8. Artifact authorization and run/step binding.
9. No blind replay of uncertain sensitive side effects.
10. No client-controlled production authority.

Infrastructure adds:

11. Separate staging/production credentials.
12. Private data-plane services.
13. TLS/public HTTPS ingress.
14. No default production secrets.
15. No production auto-migrate.
16. Least-privilege database/runtime credentials.
17. Secrets excluded from telemetry.
18. Production object bucket not publicly readable.
19. Immutable release identity by Git SHA.

## 22. Phase decomposition

Phase 7 will not be implemented as one large change. Each subphase receives its own branch, tests, review and merge gate.

- **7A — Production Environment & Release Foundation:** production configuration; startup safety validation; API/worker role split; production Docker hardening; environment separation; Vercel/Railway deployment definitions; staging deployment; explicit migration job; release metadata/Git SHA; production promotion workflow.
- **7B — Managed Data & Object Storage:** managed PostgreSQL/pgvector contract; Redis production config; `IArtifactObjectStore`; local provider migration; S3-compatible provider; reconciliation/recovery; storage integration tests.
- **7C — Observability & Alerting:** ASP.NET OpenTelemetry; FastAPI/OpenTelemetry instrumentation; OTEL Collector; traces/metrics/log correlation; operational metrics; dashboards; alert rules; secret/high-cardinality audit.
- **7D — Backup, Restore & Disaster Recovery:** database backup automation; retention; artifact recovery; restore drill; rollback procedure; DR runbook; RPO/RTO evidence.
- **7E — Performance & Production Release Gate:** k6 performance suite; concurrency/load validation; staging soak/smoke; production promotion; post-deploy verification; final Phase 7 evidence report.

## 23. Acceptance matrix

Phase 7 is not complete unless all applicable gates pass.

**Application:** backend Debug PASS; backend Release PASS; frontend lint/tests/typecheck/build PASS; AI tests PASS; deterministic RAG evaluation PASS; formatting/diff checks PASS.

**Production safety:** production rejects development/default JWT values; production rejects missing required secrets/config; `Database__AutoMigrate=false`; production API starts without local-only dependencies; secrets absent from repository and logs.

**Infrastructure:** staging environment deployed independently; production environment configuration independently scoped; internal services inaccessible publicly unless explicitly required; API → AI private connectivity works; API/worker → PostgreSQL works; pgvector readiness passes; Redis degradation does not corrupt durable state.

**Storage:** S3-compatible artifact round trip passes; checksum verification passes; tenant isolation passes; crash/reconciliation cases pass; no public artifact exposure.

**Deployment:** migration job passes; staging smoke passes; exact immutable artifact promoted; rollback to previous application version is demonstrated.

**Observability:** trace from API reaches collector/backend; metrics reach collector/backend; deployment environment/SHA visible; secrets/content absent from telemetry; alert test fires and resolves.

**Recovery:** backup created; backup restored to isolated environment; restored API becomes ready; tenant/workflow/artifact integrity checked; RPO/RTO targets measured and documented.

**Performance:** 25 RPS / 10-minute non-AI gate passes; p95 <= 750 ms for selected endpoints; unexpected 5xx < 1%; no tenant leakage; no durable workflow duplication/corruption.

## 24. Explicit non-goals

Phase 7 does not introduce: Kubernetes; service mesh; multi-region active/active deployment; automatic database failover written by ICEHOTT; custom secrets manager; custom observability backend; billing; multi-cloud orchestration; retry behavior that weakens existing Phase 4.5/6 safety guarantees. These can be added only after measured operational need.

## 25. Final outcome

After Phase 7, ICEHOTT must be demonstrably deployable as:

```
Vercel:
  Next.js web

Railway:
  ASP.NET public API
  ASP.NET background worker
  FastAPI AI runtime
  OpenTelemetry Collector
  PostgreSQL + pgvector
  Redis

Managed object storage:
  S3-compatible private artifact bucket
```

with isolated staging and production configuration, explicit migrations, immutable releases, external secrets, telemetry, alerting, backup/restore evidence, performance evidence and a documented disaster-recovery process. Phase 7 is complete only after the production release gate has executable evidence and the documentation matches the deployed behavior.

## Implementation status

| Subphase | Status |
|---|---|
| 7A Production Environment & Release Foundation | Implemented on `phase-7a-production-environment-release-foundation` (see the 7A document) |
| 7B Managed Data & Object Storage | Not started |
| 7C Observability & Alerting | Not started |
| 7D Backup, Restore & Disaster Recovery | Not started |
| 7E Performance & Production Release Gate | Not started |
