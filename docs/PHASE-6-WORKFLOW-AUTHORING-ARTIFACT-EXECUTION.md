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

## Review hardening (post-6A audit)

An independent review of `fef8143` found and fixed the following. Each has a real-PostgreSQL regression test in `WorkflowPhase6HardeningPostgresTests` or an HTTP contract test in `WorkflowPhase6ContractTests`.

1. **Version activation failed for every second version.** EF issued the new version's `Active` UPDATE before the previous version's retirement, so the partial unique index (one active version per definition) rejected it and the API returned `409`. Retirement is now flushed first, inside the same transaction. Concurrent activation of two drafts leaves exactly one `Active` version.
2. **Cancelling a run that owned a pending tool execution orphaned the tool.** The store finalized `Waiting`/`Queued` runs directly, leaving an approvable `tool_executions` row. Runs whose active step owns a tool execution now stay `cancellation requested`; the durable queue hands them to the runner, which cancels the tool first.
3. **Artifact binding was checked outside the insert transaction.** A role demotion, cancellation, or step completion between the service pre-check and the insert could still admit the artifact. The binding (membership, run-as/Admin authority, run `Waiting` on `Artifact`, exact step `WaitingForArtifact`, no cancellation requested, no existing `Ready` artifact) is now revalidated in `TryAddWithinQuotaAsync` under `FOR SHARE` locks on the membership and run rows.
4. **Idempotency replay disclosed other members' runs.** The key is scoped to workspace + definition, so a second member replaying it received the first member's run. A replay by a different requester is now `409 idempotency_conflict`; a unique-key loss at commit resolves to a replay instead of a generic conflict.
5. **Audit gap.** Direct cancellation now records both `CancellationRequested` and `RunCancelled`.

Canonical JSON sorts object keys only; number formatting is preserved, so `1` and `1.0` hash differently by design.

### Evidence (2026-09-29, branch `phase-6-workflow-authoring-artifact-execution`)

- Debug and Release backend suites: 496/496 passed each, with `ICEHOTT_POSTGRES_TEST_CONNECTION` set to an isolated `pgvector/pgvector:pg16` container. 74 PostgreSQL-gated tests ran (about 305 s cumulative); without pgvector the same tests fail at migration, which confirms they reach the database. The PostgreSQL tests `return` silently when the variable is unset, so a green run without it proves nothing.
- `dotnet format --verify-no-changes`, `git diff --check`, Release build (0 warnings) and `dotnet ef migrations has-pending-model-changes` (no schema change) pass.
- Frontend lint, `tsc --noEmit`, vitest (11/11) and `next build` pass; no frontend change is required because the upload fields are optional additions.
