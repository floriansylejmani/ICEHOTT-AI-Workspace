# Phase 5F — Scheduled & Triggered Execution Release Evidence

Date: 2026-09-26
Branch: `phase-5f-scheduled-triggers`
Base: `87c09bc`

## Status

**PASS / ready for integration.**

Phase 5F adds durable scheduled workflow execution on top of the Phase 5C runner, Phase 5D artifact storage and Phase 5E checkpoint decision plane.

## Delivered scheduler foundation

- bounded 5-field cron parser/calculator
- server-side schedule validation
- timezone validation
- DST fallback ambiguity handling
- DST spring-forward invalid-local-time handling
- minimum schedule interval enforcement
- trigger create/list/enable/disable API
- server-bound `RunAsUserId`
- workspace active-trigger quota
- scheduler hosted worker
- durable trigger fire ledger
- deterministic fire keys
- deterministic run idempotency keys

## Cron and timezone semantics

Supported cron fields:

- minute
- hour
- day of month
- month
- day of week

Supported field forms:

- wildcard
- list
- range
- step

The scheduler intentionally does not execute arbitrary expressions or scripts.

Timezone rules are explicit:

- invalid timezone identifiers are rejected
- an invalid/skipped local time during spring-forward advances to the next valid occurrence
- an ambiguous local time during fall-back resolves to one UTC occurrence only
- the same logical local occurrence is not fired twice

## Administration and authorization

Trigger administration is server-authoritative:

- only workspace Admin/Owner can create, enable or disable a trigger
- clients do not supply `RunAsUserId`
- `RunAsUserId` is bound server-side to the trigger creator
- workflow definition must be Active
- pinned workflow version must be executable
- schedule/timezone are validated by the server
- active trigger quota is enforced per workspace

Trigger administration persistence revalidates authority in the same database transaction:

- workspace row lock serializes quota decisions
- actor membership/role is revalidated
- run-as membership/role is revalidated
- concurrent enable/disable operations are protected by optimistic concurrency
- actor demotion/removal racing enable fails closed

## Durable firing and crash recovery

Due triggers are claimed using PostgreSQL `FOR UPDATE SKIP LOCKED`.

For each due occurrence:

1. one durable `WorkflowTriggerFire` row is created
2. the trigger's `NextRunAtUtc` advances past the current scheduler time
3. at most one missed occurrence is caught up
4. the fire remains recoverable in `Claimed` state
5. a later scheduler cycle creates the workflow run transactionally
6. the fire transitions to `RunCreated`
7. the run uses an idempotency key derived from the fire key

Before run creation, the scheduler revalidates:

- trigger still enabled
- workflow still Active
- pinned version still executable
- run-as user is still a workspace member
- run-as role still meets `MinimumRunRole`

Stale run-as authority fails closed and disables the trigger.

## Audit events

Added durable audit events for:

- `TriggerCreated`
- `TriggerEnabled`
- `TriggerDisabled`
- `TriggerFired`
- `TriggerFireFailed`
- `TriggerSkipped`

## Focused test evidence

Focused Phase 5F gate: **23 / 23 PASS**.

Coverage includes:

- cron wildcard/list/range/step parsing
- invalid cron rejection
- invalid timezone rejection
- minimum interval rejection
- DST fall-back fires once
- DST spring-forward skips invalid local time
- active-trigger quota
- concurrent enable quota race
- concurrent due-trigger claims
- concurrent processing of one Claimed fire creates exactly one run
- one-catch-up behavior after downtime
- disabled-after-claim produces no run
- run-as demotion/removal fails closed
- actor demotion/removal racing enable fails closed
- pinned retired version remains executable
- duplicate-fire/run prevention

## Full regression evidence

- backend Debug: **476 / 476 PASS**
- backend Release: **476 / 476 PASS**
- Release build: **0 warnings / 0 errors**
- EF pending-model-changes gate: **no changes**
- frontend `npm ci`: **0 vulnerabilities**
- frontend ESLint: **PASS**
- frontend Vitest: **8 / 8 PASS**
- frontend production build: **PASS**
- AI pytest: **5 / 5 PASS**
- Docker API image build: **PASS**
- Docker AI image build: **PASS**
- final API image force-recreated in Compose: **PASS**
- API `/health`: **200**
- API `/ready`: **200**
- AI `/health`: **200**
- scheduler worker startup: **PASS**
- API fatal/fail log lines in final smoke: **0**

Phase 5F changes no database schema; no new EF migration is required.

## Real API + scheduler E2E smoke

A real Release API container was run against an isolated PostgreSQL database.

The live flow proved:

1. create scheduled trigger
2. list trigger
3. server binds run-as identity
4. enable trigger
5. force a persisted missed occurrence
6. scheduler creates exactly one catch-up fire
7. fire creates exactly one workflow run
8. workflow runner processes that scheduled run
9. scheduled workflow reaches its checkpoint wait
10. TriggerFired audit event is written once
11. create/enable/disable audit trail is present
12. disable endpoint prevents future scheduling

Result: **PASS**.

## Restart recovery smoke

With the scheduler stopped, a real `Claimed` fire was persisted. The API container was then restarted with the scheduler enabled.

After restart:

- fire status: `RunCreated`
- fire rows for the recovery fire: **1**
- workflow runs linked to that fire: **1**
- recovered workflow run progressed to `Waiting`
- recovered run created exactly one checkpoint

Result: **PASS**.

This demonstrates recovery of a persisted claimed occurrence without duplicate run creation.

## Cleanliness

- `dotnet format --verify-no-changes`: **PASS**
- `git diff --check`: **PASS**
- test credential/smoke-key scan: **PASS**

## Explicit deferrals

- workflow/artifact frontend experience — Phase 5G
