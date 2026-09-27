# Phase 6 — Workflow Authoring & Artifact-Gated Execution

**Date:** 2026-09-27
**Baseline:** `04ba464` — Phase 5H final hardening release
**Packet:** Phase 6A

## Objective

Phase 6A closes the write-side gap left intentionally by Phase 5. It exposes authenticated workflow authoring/run mutation APIs and makes artifact workflow steps durable end-to-end.

The design preserves the Phase 4.5/5 security model: tenant scope is server-side, membership is revalidated at mutation time, durable work is idempotent, and audit records remain append-only.

## Scope

- Create workflow definitions.
- Create immutable workflow versions from validated canonical JSON.
- Activate one version and archive definitions.
- Start runs with caller-supplied idempotency keys.
- Cancel active runs.
- Expose retry as fail-closed until retry semantics are explicitly designed.
- Bind artifact uploads to the exact waiting workflow run/step.
- Wake durable runs when the bound artifact becomes ready.
- Complete artifact steps with redacted metadata only, never artifact content.
## Security invariants

1. Workflow definition/version/activation/archive mutations require at least workspace `Admin`.
2. A non-member receives nondisclosing `workspace_not_found`.
3. Run creation revalidates the caller's current role against `MinimumRunRole` inside the persistence transaction.
4. Run idempotency is scoped to workspace + workflow definition + idempotency key.
5. Cancellation is allowed only to the requester or a current workspace `Admin`.
6. Artifact binding requires both `WorkflowRunId` and `StepRunId`; partial binding is rejected.
7. A bound artifact must target an existing artifact step in the same workspace/run.
8. Only the run-as user or a workspace `Admin` may satisfy an artifact step.
9. Upload is accepted only while the run is waiting on that exact artifact step.
10. Ready artifacts wake the durable queue without polling arbitrary external state.
11. Workflow JSON is parsed, canonicalized, and SHA-256 hashed before persistence.
12. Workflow lifecycle/run mutations emit append-only workflow audit events.

## HTTP contract

- `POST /api/workspaces/{workspaceId}/workflows`
- `POST /api/workspaces/{workspaceId}/workflows/{workflowId}/versions`
- `POST /api/workspaces/{workspaceId}/workflows/{workflowId}/versions/{versionId}/activate`
- `POST /api/workspaces/{workspaceId}/workflows/{workflowId}/archive`
- `POST /api/workspaces/{workspaceId}/workflow-runs`
- `POST /api/workspaces/{workspaceId}/workflow-runs/{runId}/cancel`
- `POST /api/workspaces/{workspaceId}/workflow-runs/{runId}/retry`

`retry` currently returns `run_retry_not_supported` rather than replaying work with undefined safety semantics.
## Artifact execution state

`Running -> Waiting(Artifact) / WaitingForArtifact -> Ready bound artifact -> leased again -> StepSucceeded -> workflow continues`

The queue considers an artifact-waiting run due only when a `Ready` artifact exists for the same workspace, workflow run, and current step run.

## Acceptance matrix

Before Phase 6A integration:

- Debug and Release backend suites pass.
- Workflow mutation integration tests cover create/version/activate/run/idempotency/cancel.
- Role gating and cross-tenant nondisclosure pass.
- Real PostgreSQL proves concurrent run-request idempotency.
- Real PostgreSQL proves current minimum-run-role revalidation.
- Real PostgreSQL proves artifact wait -> ready artifact -> queue wake -> run success.
- Existing Phase 5 race/restart/audit tests remain green.
- `dotnet format --verify-no-changes` and `git diff --check` pass.
- GitHub Actions is green for the exact commit before integration.

## Deferred Phase 6 work

- Explicit retry/replay policy by terminal outcome and step risk.
- Visual workflow authoring/editor UX.
- Email/notification delivery and event-driven messaging integration.
- Additional production artifact providers and retention automation.
- Operational workflow analytics and SLA dashboards.
