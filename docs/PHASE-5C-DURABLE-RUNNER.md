# Phase 5C — Durable Runner & Recovery Release Evidence

Date: 2026-09-26
Branch: `phase-5c-durable-runner`
Base: `2209f2c`
Migration: `20260926154306_Phase5CDurableRunner`

## Status

**PASS / ready for integration.**

Phase 5C adds durable execution, recovery, fencing and restart safety on top of
the Phase 5B workflow persistence foundation. It does not add artifact byte
storage, checkpoint approval APIs, scheduling APIs, or workflow UI.

## Delivered runtime

- PostgreSQL-backed `IWorkflowRunQueue`
- atomic claim with `FOR UPDATE SKIP LOCKED`
- lease owner, expiry and monotonic `LeaseGeneration`
- fenced heartbeat and fenced state commits
- durable cancellation request fields
- delay and retry-backoff resume
- workflow run-as membership/role revalidation before every new step
- stable tool idempotency derived from workflow run + persisted step attempt
- child-DI-scope tool invocation preserving Phase 4.5 authorization and commit
  boundaries
- tool-wait recovery without blind replay
- `OutcomeUnknown` propagation for uncertain sensitive side effects
- bounded retry policy for safe failures
- deterministic workflow-plan validation
- hosted `WorkflowRunnerWorker` with lease heartbeat
## Workflow definition/runtime validation

The runtime parser enforces:

- bounded definition size and step count
- unique step keys
- known step types
- valid dependencies
- exactly one deterministic entry step
- acyclic dependency graph
- bounded retry policy
- bounded delay duration
- typed tool arguments
- checkpoint role/separation metadata
- duplicate JSON-property rejection

Artifact execution remains intentionally unavailable until Phase 5D. Checkpoint
pause state is supported; transactional human decision APIs remain Phase 5E.

## PostgreSQL safety guarantees

Real PostgreSQL tests prove:

- two concurrent workers cannot claim one run twice
- stale workers are fenced after reclaim
- heartbeat requires the current lease generation
- due delay waits resume and future waits do not
- cancellation wakes a durable checkpoint wait
- an expired run with a still-running tool is not blindly replayed
- tool-wait runs wake only after the linked tool reaches a terminal state
- Phase 5C migration rolls back to Phase 5B and reapplies successfully
## Processor/recovery evidence

Real PostgreSQL processor tests prove:

- delay workflows survive a wait/restart boundary
- a simulated crash before tool persistence reuses the same logical step and
  stable idempotency key after reclaim
- retryable read-only tool failures create a new bounded attempt
- run-as membership removal fails closed before a new step starts
- cancellation of a waiting checkpoint is durable
- sensitive `OutcomeUnknown` stops the workflow without retry

Focused Phase 5C gate: **20 / 20 PASS**.

## Full regression evidence

- backend Debug: **426 / 426 PASS**
- backend Release: **426 / 426 PASS**
- Release solution build: **0 warnings / 0 errors**
- PostgreSQL integration tests enabled against isolated
  `pgvector/pgvector:pg16`
- frontend `npm ci`: **0 vulnerabilities**
- frontend ESLint: **PASS**
- frontend Vitest: **8 / 8 PASS**
- frontend production build: **PASS**
- AI pytest: **5 / 5 PASS**
- Docker API build: **PASS**
- Docker AI build: **PASS**
- API `/health`: **200**
- API `/ready`: **200**
- AI `/health`: **200**
- hosted workflow runner startup: **PASS**
## Migration evidence

- idempotent EF migration script generated through Phase 5C
- script applied twice to the same clean database: **PASS**
- latest migration:
  `20260926154306_Phase5CDurableRunner`
- `CancellationRequestedAtUtc`: present
- `CancellationRequestedByUserId`: present
- cancellation/lease scan index: present
- Phase 5C -> Phase 5B rollback -> Phase 5C reapply: **PASS**

## Live restart-recovery smoke

An isolated PostgreSQL database was seeded with a single 30-second delay
workflow and processed by a real Release API host with `WorkflowRunnerWorker`
enabled.

Before process restart:

- run status: `Waiting`
- wait reason: `Delay`
- lease generation: `1`

After the resume instant and API restart:

- run status: `Succeeded`
- workflow step rows: **1**
- maximum step attempt: **1**
- step status: `Succeeded`
- lease generation: **2**

This demonstrates real process restart recovery without duplicating the logical
step attempt.

## Cleanliness

- `dotnet format --verify-no-changes`: **PASS**
- `git diff --check`: **PASS**
- test credential/smoke-key scan: **PASS**

## Explicit deferrals

- artifact byte storage, checksum finalization and orphan cleanup — Phase 5D
- transactional checkpoint approval/rejection APIs — Phase 5E
- scheduled trigger evaluation and firing worker — Phase 5F
- workflow/artifact frontend — Phase 5G
