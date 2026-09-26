# Phase 5G — Workflow & Artifact Frontend Experience

Date: 2026-09-26
Branch: `phase-5g-workflow-artifact-frontend`
Base: `19fd192`

## Status

**PASS / ready for integration.**

Phase 5G exposes the durable Phase 5 workflow, checkpoint, trigger, and artifact
capabilities through the authenticated Next.js workspace while preserving the
server-authoritative security boundaries established in Phases 4.5–5F.

## Delivered frontend experience

- first-class `Workflows` and `Artifacts` workspace navigation
- workflow definition list and selected workflow detail
- pinned workflow version metadata without exposing definition JSON
- recent workflow run list and durable step timeline
- current run/wait/error metadata with bounded safe projections
- checkpoint approve/reject actions with optional bounded decision reason
- scheduled trigger list, create, enable, and disable controls
- Admin/Owner trigger-management affordances while the API remains authoritative
- artifact list with checksum/size/status metadata
- multipart artifact upload with client idempotency keys
- authenticated artifact content download
- authenticated artifact delete flow

## Safe frontend API surface

Added authenticated read endpoints:

- `GET /api/workspaces/{workspaceId}/workflows`
- `GET /api/workspaces/{workspaceId}/workflows/{workflowId}`
- `GET /api/workspaces/{workspaceId}/workflow-runs`
- `GET /api/workspaces/{workspaceId}/workflow-runs/{runId}`

The new projections intentionally do **not** expose:

- workflow `DefinitionJson`
- workflow-step `InputJson` or `OutputJson`
- raw workflow/step error messages
- storage keys or filesystem paths

Every read verifies current workspace membership. Cross-tenant reads return the
same non-disclosing workspace/resource behavior used by the existing API.

Checkpoint, trigger, and artifact mutations continue to call the existing
Phase 5D–5F services. Frontend role checks only control affordances; they never
grant authority.

## Test evidence

- focused workflow-experience API tests: **2 / 2 PASS**
- safe-projection test proves secret-bearing definition/input/output JSON is absent
- cross-tenant workflow/read tests return non-disclosing `404` behavior
- frontend Vitest: **11 / 11 PASS**
- workflow UI test covers run timeline, checkpoint approval, and trigger creation
- artifact UI test covers multipart upload/idempotency and delete

## Regression evidence

- backend Debug: **479 / 479 PASS**
- backend Release: **479 / 479 PASS**
- frontend ESLint: **PASS**
- frontend production build: **PASS**
- Next.js route generation: **PASS**
- AI pytest: **5 / 5 PASS**
- Release solution build: **0 warnings / 0 errors**
- `dotnet format --verify-no-changes`: **PASS**
- `git diff --check`: **PASS**

Phase 5G adds no database schema and requires no EF migration.

## Security invariants preserved

- authorization remains server-side for every workspace-owned operation
- checkpoint separation-of-duty and minimum role checks remain server-enforced
- trigger Admin/Owner and run-as revalidation remain server-enforced
- artifact delete creator/Admin/Owner authorization remains server-enforced
- artifact bytes are downloaded only through the authenticated content endpoint
- no storage key or direct filesystem path is exposed to the browser
- workflow definition and step payload JSON are not returned by the experience API

## Explicit deferral

- Phase 5H — final hardening, full-stack release smoke, independent review,
  Docker/CI release evidence, and final Phase 5 integration gate
