# Phase 4.5 — Finalization and Release Evidence

Date: 2026-09-25
Branch: `phase-4.5-finalization`
Integration base: `65b95b7`

## Decision

**PASS for Phase 4.5 branch finalization.** The branch closes the remaining
membership-authorization race and Packet E audit-immutability/adversarial-test
work. This document does not merge `main`; merge remains a separate repository
operation.

## Security changes finalized

- Approval persistence revalidates requester and approver memberships inside the
  same database transaction that commits the approval.
- PostgreSQL uses membership row locks so removal or demotion racing approval
  cannot authorize a stale decision.
- Requester authority loss preserves the existing
  `requester_no_longer_authorized` API contract.
- Approver authority loss fails closed as `forbidden`.
- Prompt text claiming system approval or embedding a sensitive tool call cannot
  bypass server-side registry, role, approval, schema, or workspace checks.
## Packet E — immutable audit

Migration `20260925213117_Phase45AuditImmutability` installs PostgreSQL
BEFORE UPDATE OR DELETE triggers on:

- `tool_execution_audit_events`
- `tool_policy_audit_events`

Both triggers call a database function that rejects mutation with SQLSTATE
`55000`. Normal append operations remain allowed. The Down migration removes
both triggers and the function.

PostgreSQL integration coverage proves:

- UPDATE of execution audit rows is rejected;
- DELETE of execution audit rows is rejected;
- UPDATE of policy audit rows is rejected;
- DELETE of policy audit rows is rejected;
- new execution and policy audit events can still be appended;
- rollback to `20260925204435_Phase45ToolQuotas` removes the guards;
- reapplying migrations restores both guards.
## Verification evidence

- Backend Debug: **389/389 passed**.
- Backend Release: **389/389 passed**.
- Fresh isolated PostgreSQL Release run with PostgreSQL tests enabled:
  **389/389 passed**.
- Focused Phase 4.5 PostgreSQL policy/quota/audit gate: **12/12 passed**.
- AI service pytest: **5/5 passed**.
- Frontend ESLint: **passed**.
- Frontend Vitest: **8/8 passed**.
- Frontend production build: **passed**.
- `npm ci`: **0 vulnerabilities**.
- EF idempotent migration script: generated successfully and contains the
  Phase 4.5 audit migration and both append-only triggers.
- Docker Compose build: API image **built**, AI image **built**.
- Benchmark harness `--dry-run`: **passed**, with no activation performed.
- `git diff --check`: clean.
- Credential scan found only documented CI/development placeholders and the
  intentionally fake secret used by the redaction regression test.

A first full PostgreSQL run against the long-lived benchmark database exposed
two Phase 3.6B unique-index collisions caused by pre-existing test data. The same
complete Release suite was rerun against a newly created isolated database and
passed 389/389, confirming there is no Phase 4.5 regression.
## Release invariants

Phase 4.5 now preserves these release-blocking invariants:

1. Workspace membership and policy authorization remain server-owned.
2. Pending sensitive writes cannot execute after requester or approver authority
   is lost before approval commit.
3. Concurrent approvals remain at-most-once.
4. Tool admission quotas remain transactional and tenant/tool scoped.
5. Credentials and secret-shaped inputs remain rejected/redacted by the Packet C
   boundary.
6. Running writes are bounded by Packet D lifecycle/recovery semantics and are
   never automatically replayed after an uncertain outcome.
7. Tool execution and policy audit history is append-only at the PostgreSQL
   database boundary.
8. Prompt/retrieved text cannot manufacture approval or elevate a caller.

## Merge posture

The release candidate is ready for review from
`phase-4.5-finalization`. No direct merge to `main` was performed as part of
this finalization.
