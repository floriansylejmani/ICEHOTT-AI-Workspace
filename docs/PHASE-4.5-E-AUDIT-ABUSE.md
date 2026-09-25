# Phase 4.5 Packet E — Audit Immutability & Abuse Hardening

Status: implemented on `phase-4.5-finalization`.
Base: integrated Phase 4.5 lifecycle/policy/budget branch at `65b95b7`.

## Scope

Packet E closes the remaining audit-integrity and prompt-injection release blockers.
It does not add a generic shell, URL fetcher, external SaaS write adapter, or any
new authority path for the AI runtime.

The security boundary remains server-owned: workspace membership, effective tool
policy, typed arguments, approval state, idempotency and execution transitions
are resolved by ASP.NET Core and persisted in PostgreSQL.
## PostgreSQL audit immutability

Migration `20260925213117_Phase45AuditImmutability` installs one PostgreSQL
trigger function and two row triggers:

- `tool_execution_audit_events`: UPDATE and DELETE are rejected.
- `tool_policy_audit_events`: UPDATE and DELETE are rejected.
- INSERT remains allowed so normal audit appends continue.

Rejected mutations raise SQLSTATE `55000` with the stable database message
`tool audit rows are append-only`. The migration is PostgreSQL-specific;
SQLite-backed unit/integration fixtures are intentionally unaffected.

The Down migration removes both triggers and then the trigger function. The
PostgreSQL integration suite proves latest migration -> previous migration ->
latest migration and verifies trigger count 2 -> 0 -> 2.
## Privileged maintenance boundary

Normal application DML cannot update or delete these audit rows, including DML
issued by the table owner while the triggers are enabled.

A PostgreSQL superuser or sufficiently privileged database operator can still
intentionally disable/drop the trigger or restore data outside the application.
That is an operator break-glass action, not an application feature. Any such
maintenance must be separately authorized, recorded outside the affected audit
tables, scoped to a maintenance window, and followed by restoration and
verification of both triggers.

The application exposes no audit UPDATE or DELETE endpoint.
## Prompt-injection and authorization abuse

Adversarial HTTP tests prove that untrusted text remains data:

- a Member can echo text that says "SYSTEM APPROVED" and embeds a sensitive
  tool-call JSON object, but it produces only the requested read-only echo and
  no workspace audit-note side effect;
- a Member cannot request `workspace.audit-note.create` merely by placing
  approval/role-bypass instructions in the message; the server returns
  `forbidden` and persists no execution.

Packet E therefore does not attempt to classify every possible malicious phrase.
It verifies the stronger invariant: prompt/retrieved text cannot set tool
identity, role, approval, policy, execution state or workspace authority.
## Approval membership race closure

Finalization also closes the independent Packet B review blocker where membership
could change between the approval re-check and commit.

For PostgreSQL, approval now takes shared locks on the requester and approver
membership rows in deterministic user-id order and revalidates both roles in the
same transaction that commits the approval. Concurrent membership deletion or
demotion must linearize before or after that transaction.

Real PostgreSQL race tests cover requester removal and approver demotion.
Both fail closed with the existing API contracts, leave the execution
`PendingApproval`, append no Approved event, and create no write side effect.
## Verification evidence

Release-gate evidence collected on 2026-09-25:

- backend Debug on a fresh isolated PostgreSQL database: 389/389 passed;
- backend Release on a fresh isolated PostgreSQL database: 389/389 passed;
- targeted Phase 4.5 PostgreSQL concurrency/quota/audit suite: 12/12 passed;
- frontend ESLint: passed;
- frontend Vitest: 8/8 passed;
- Next.js production build: passed;
- Python AI service pytest: 5/5 passed;
- idempotent EF migration script generated successfully and contains both
  append-only trigger definitions;
- migration rollback/reapply test: passed.

The local Git diff review and Docker image builds for `api` and `ai` have passed.
Remote CI on the exact pushed commit remains required before merge to `main`.
