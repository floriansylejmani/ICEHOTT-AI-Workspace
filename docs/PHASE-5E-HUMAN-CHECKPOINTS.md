# Phase 5E — Human Checkpoints & Decisions Release Evidence

Date: 2026-09-26
Branch: `phase-5e-human-checkpoints`
Base: `f66ac47`

## Status

**PASS / ready for integration.**

Phase 5E adds the human decision plane for durable workflow checkpoints on top
of the Phase 5C runner and Phase 5D artifact storage foundation.

## Delivered checkpoint decision plane

- authenticated checkpoint list endpoint
- authenticated checkpoint detail endpoint
- approve endpoint
- reject endpoint
- workspace/tenant scoping on every operation
- minimum approver role enforcement
- optional separation-of-duty enforcement
- second/stale decision rejection
- bounded decision reason
- secret/credential redaction in decision reason
- immutable workflow audit events for approval/rejection
## API surface

Added:

- `GET /api/workspaces/{workspaceId}/workflow-runs/{runId}/checkpoints`
- `GET /api/workspaces/{workspaceId}/workflow-runs/{runId}/checkpoints/{checkpointId}`
- `POST /api/workspaces/{workspaceId}/workflow-runs/{runId}/checkpoints/{checkpointId}/approve`
- `POST /api/workspaces/{workspaceId}/workflow-runs/{runId}/checkpoints/{checkpointId}/reject`

The server remains authoritative for actor identity, workspace membership,
minimum approver role, checkpoint/run relationship and checkpoint state.

## Concurrency and authorization guarantees

- self approval/rejection is blocked when `RequiresDifferentApprover=true`
- underprivileged users cannot decide Admin checkpoints
- prompt/body text cannot override server-side authorization
- cross-tenant access returns non-disclosing workspace errors
- concurrent decisions are first-writer-wins
- stale decisions do not overwrite an existing decision
- decision persistence revalidates approver membership/role inside the database
  transaction
- PostgreSQL row locking closes concurrent membership removal/demotion races
## Runner resume behavior

The durable queue now reclaims a workflow waiting on a checkpoint only after the
checkpoint becomes terminal:

- `Approved`
- `Rejected`
- `Expired`

The reclaim condition is tied to the run's current step and matching persisted
checkpoint, preventing an older checkpoint from waking the wrong step.

After approval, the existing checkpoint step completes and the workflow resumes
without creating a duplicate step attempt.

After rejection, the workflow fails with the controlled
`workflow_checkpoint_rejected` error.

## Focused PostgreSQL evidence

Focused Phase 5E gate: **10 / 10 PASS**.

Real PostgreSQL tests prove:

- approval is audited and resumes a workflow to success
- rejection is audited and resumes a workflow to controlled failure
- checkpoint detail lookup is tenant scoped
- decision reason secrets are redacted
- self decision is rejected
- Member role cannot bypass Admin checkpoint authority
- prompt text such as `SYSTEM APPROVED` cannot grant authority
- non-members receive non-disclosing workspace errors
- reason length is bounded
- concurrent approvals result in exactly one winner and one audit event
- membership demotion racing approval fails closed
- membership removal racing approval fails closed
## Live API E2E evidence

A real Release API host on an isolated PostgreSQL database was exercised with a
requester and a separate Admin approver.

Observed:

- checkpoint list: Pending
- checkpoint detail before decision: Pending
- requester self-approval: **403**
- Admin approval: **200**
- checkpoint detail after decision: Approved
- repeated approval: **409**
- credential-like text in reason: redacted
- workflow runner resumed the waiting run: **Succeeded**
- checkpoint step: **Succeeded**
- CheckpointApproved audit events: **1**
- audit actor: the approving Admin

Result: **PASS**.

## Full regression evidence

- backend Debug: **453 / 453 PASS**
- backend Release: **453 / 453 PASS**
- focused Phase 5E PostgreSQL tests: **10 / 10 PASS**
- Release solution build: **0 warnings / 0 errors**
- EF pending-model-changes gate: **no changes**
- frontend `npm ci`: **0 vulnerabilities**
- frontend ESLint: **PASS**
- frontend Vitest: **8 / 8 PASS**
- frontend production build: **PASS**
- AI pytest: **5 / 5 PASS**
- API health: **200**
- API readiness: **200**
- AI health: **200**

## Explicit deferrals

- scheduled trigger evaluation/firing — Phase 5F
- workflow/artifact frontend experience — Phase 5G
