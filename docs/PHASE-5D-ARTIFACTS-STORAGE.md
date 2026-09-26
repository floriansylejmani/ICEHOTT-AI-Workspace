# Phase 5D — Artifacts & File Storage Release Evidence

Date: 2026-09-26
Branch: `phase-5d-artifacts-storage`
Base: `2a145a9`
Migration: `20260926175544_Phase5DArtifactsStorage`

## Status

**PASS / ready for integration.**

Phase 5D adds durable workspace-scoped artifact metadata and byte storage on top
of the Phase 5B/5C workflow platform. The implementation keeps PostgreSQL as the
metadata authority and stores bytes behind an `IArtifactStore` abstraction.

Checkpoint decision APIs, schedule evaluation, and workflow UI remain outside
this packet.

## Delivered storage foundation

- `IArtifactStore` abstraction
- local filesystem provider with streaming writes
- server-computed SHA-256 and byte count
- bounded upload size enforcement
- two-phase staging and final object paths
- idempotent commit when a staged object was already moved
- checksum/size verification before finalization and reads
- strict storage-key shape validation
- root-escape and traversal protection
- staging/orphan cleanup
- persistent Docker named volume `artifact_data`
## Durable artifact lifecycle

Artifact metadata now persists:

- `Pending`
- `Ready`
- `Failed`
- `Deleted`
- staging key
- idempotency key
- failure timestamp
- logical delete timestamp
- physical storage delete timestamp

The upload flow is:

`stream -> staging -> quota/idempotency transaction -> Pending metadata ->
verified storage commit -> Ready metadata`.

If storage commit is temporarily unavailable after metadata insertion, the
artifact remains recoverable instead of being silently lost.

## Authorization, quota and idempotency

- every list/get/content/delete operation is workspace scoped
- non-members receive non-disclosing workspace/artifact results
- delete requires artifact creator or workspace Admin/Owner
- workspace count and byte quotas are checked under a PostgreSQL workspace row
  lock
- concurrent uploads cannot overrun the count/byte quota
- `(WorkspaceId, IdempotencyKey)` is unique when a key is supplied
- same idempotency key + same logical artifact replays the original artifact
- same key + different content/metadata returns an idempotency conflict
- tombstoned artifacts are excluded from normal workspace listings
## Recovery and maintenance

`ArtifactMaintenanceWorker` and `ArtifactMaintenanceService` recover:

- stale Pending metadata with surviving staging bytes
- bytes moved to final storage before the Ready database save
- Failed artifacts with leftover staging files
- Deleted artifacts whose physical bytes still require cleanup
- old orphan staging files

Maintenance is bounded and configurable through `ArtifactStorage` settings.

## API surface

Added authenticated workspace endpoints:

- `POST /api/workspaces/{workspaceId}/artifacts/upload`
- `GET /api/workspaces/{workspaceId}/artifacts`
- `GET /api/workspaces/{workspaceId}/artifacts/{artifactId}`
- `GET /api/workspaces/{workspaceId}/artifacts/{artifactId}/content`
- `DELETE /api/workspaces/{workspaceId}/artifacts/{artifactId}`

Storage keys are never returned to clients.

## PostgreSQL and storage test evidence

Focused Phase 5D gate: **17 / 17 PASS**.

Real PostgreSQL/service tests prove:

- workspace-scoped upload/download round trip
- same-key idempotent replay without duplication
- same-key different-content conflict
- concurrent uploads obey count quota
- creator/Admin delete authorization and physical byte removal
- Pending recovery from staging after simulated crash
- final-object recovery when bytes moved before Ready metadata save
- Phase 5D migration rollback to Phase 5C and reapply

Local-store tests prove:

- server-side SHA-256 calculation
- idempotent finalization
- oversized stream rejection and temp-file cleanup
- traversal/invalid storage-key rejection
- staging/final identity mismatch rejection
- checksum mismatch rejection
## Full regression evidence

- backend Debug: **443 / 443 PASS**
- backend Release: **443 / 443 PASS**
- Release solution build: **0 warnings / 0 errors**
- frontend `npm ci`: **0 vulnerabilities**
- frontend ESLint: **PASS**
- frontend Vitest: **8 / 8 PASS**
- frontend production build: **PASS**
- AI pytest: **5 / 5 PASS**
- Docker API image build: **PASS**
- Docker AI image build: **PASS**
- API `/health`: **200**
- API `/ready`: **200**
- AI `/health`: **200**
- Artifact maintenance worker startup: **PASS**

## Migration evidence

- EF idempotent migration script generated through Phase 5D
- script applied twice to the same clean database: **PASS**
- latest migration:
  `20260926175544_Phase5DArtifactsStorage`
- staging/idempotency/storage-delete columns present
- idempotency index present
- Phase 5D -> Phase 5C rollback -> Phase 5D reapply: **PASS**
- Phase 5C -> Phase 5D upgrade preserves an existing Ready artifact:
  **before=1 / after=1**
- new nullable lifecycle columns remain null for the pre-existing artifact

## Real API + volume persistence smoke

A real Docker API instance was exercised end to end:

1. register a user
2. create a workspace
3. upload a text artifact
4. replay the upload with the same idempotency key
5. list the workspace artifacts
6. download and compare SHA-256
7. force-recreate the API container
8. download the same artifact again from the named volume
9. compare SHA-256 again
10. delete the artifact
11. verify content returns 404 and physical bytes are gone

Result: **PASS**.

The replay returned the same artifact, the byte checksum matched before and
after container recreation, and physical cleanup succeeded after delete.

## Cleanliness

- `dotnet format --verify-no-changes`: **PASS**
- `git diff --check`: **PASS**
- test credential/smoke-key scan: **PASS**
- API health after container recreation: **200**
- no unhandled/fatal API log errors observed in the release smoke

## Explicit deferrals

- transactional checkpoint approval/rejection APIs — Phase 5E
- schedule evaluation and trigger firing — Phase 5F
- workflow/artifact frontend experience — Phase 5G
