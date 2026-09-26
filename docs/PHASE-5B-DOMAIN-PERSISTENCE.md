# Phase 5B — Domain & Persistence Release Evidence

Date: 2026-09-26
Branch: `phase-5b-domain-persistence`
Base: `9a9bc47`
Migration: `20260925233754_Phase5WorkflowsFoundation`

## Status

**PASS / ready for integration.**

Phase 5B establishes the durable workflow persistence foundation defined by
the frozen Phase 5A architecture. No workflow runner, artifact byte storage,
checkpoint service, or scheduler execution is introduced in this packet.

## Delivered domain model

- `WorkflowDefinition` and immutable version snapshots
- `WorkflowVersion` with Draft / Active / Retired lifecycle
- `WorkflowRun` with durable waiting, lease generation, and terminal states
- `WorkflowStepRun` with attempts and persisted wait/retry states
- `WorkflowCheckpoint` with separation-of-duty state rules
- `Artifact` metadata lifecycle
- `WorkflowTrigger` and `WorkflowTriggerFire`
- `WorkflowAuditEvent`
## Persistence guarantees

The EF Core model and PostgreSQL migration enforce:

- composite workspace-aware foreign keys across workflow-owned resources
- one Active version per workflow definition
- unique workflow run idempotency key per workspace + workflow definition
- unique step attempt per run + step key
- one checkpoint per workflow step run
- unique trigger occurrence and unique server fire key
- non-negative lease generation and attempt constraints
- tenant-scoped query indexes and runnable/waiting scan indexes
- restrictive audit relationships
- PostgreSQL append-only workflow audit enforcement for UPDATE and DELETE

The migration includes a PostgreSQL trigger using SQLSTATE `55000` to reject
mutation of `workflow_audit_events`.

## Repository layer

Added and registered:

- `IWorkflowRepository` / `WorkflowRepository`
- `IArtifactRepository` / `ArtifactRepository`
- `IWorkflowAuditRepository` / `WorkflowAuditRepository`

All reads require an explicit workspace scope. Repository tenant-isolation is
covered by real PostgreSQL tests.
## Automated evidence

### Focused Phase 5B gate

- Domain and persistence gate: **17 / 17 PASS**
- Real PostgreSQL tests: migration rollback/reapply, audit immutability,
  cross-tenant FK rejection, Active-version uniqueness, run idempotency,
  trigger-fire dedupe, and repository tenant isolation

### Full backend

- Debug: **406 / 406 PASS**
- Release: **406 / 406 PASS**
- Release solution build: **0 warnings / 0 errors**

The full suites ran with PostgreSQL integration tests enabled against an
isolated `pgvector/pgvector:pg16` test container.

### Migration gates

- EF idempotent migration script generated successfully
- Idempotent script applied twice to the same clean database: **PASS**
- Latest migration recorded: `20260925233754_Phase5WorkflowsFoundation`
- `workflow_runs` present after migration
- append-only workflow audit trigger count: **1**
- Upgrade from Phase 4.5 schema to Phase 5B: **PASS**
- Existing baseline row before upgrade: **1**
- Existing baseline row after upgrade: **1**
- Rollback to Phase 4.5 and reapply to Phase 5B: covered by PostgreSQL test

### Cross-stack regression gate

- Frontend `npm ci`: **0 vulnerabilities**
- Frontend ESLint: **PASS**
- Frontend Vitest: **8 / 8 PASS**
- Frontend production build: **PASS**
- AI pytest: **5 / 5 PASS**
- Docker API image build: **PASS**
- Docker AI image build: **PASS**
- `git diff --check`: **PASS**

## Explicit deferrals

The following are intentionally not part of Phase 5B:

- durable claim/heartbeat/recovery worker logic — Phase 5C
- artifact byte storage and orphan cleanup — Phase 5D
- transactional checkpoint approval service — Phase 5E
- schedule evaluation and trigger worker — Phase 5F
- workflow UI — Phase 5G

Phase 5B supplies the database and repository contracts those packets build on.
