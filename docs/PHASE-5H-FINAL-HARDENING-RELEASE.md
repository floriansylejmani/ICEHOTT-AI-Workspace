# Phase 5H — Final Hardening & Release Evidence

**Date:** 2026-09-27
**Baseline:** `ef3f7dd` — Phase 5G workflow/artifact frontend experience
**Validation branch:** `phase-5h-release-isolated`

## Status

Phase 5H release validation is complete for the implemented Phase 5 contract, subject to a clean GitHub Actions run on the pushed release branch before integration.

The validation was executed from a clean, isolated Git worktree based exactly on `ef3f7dd`. The original developer worktree contained concurrent uncommitted changes from another process; those changes were preserved and were not included in this release validation.

## Release evidence

### Backend and PostgreSQL

- Full backend Debug suite with real PostgreSQL enabled: **479/479 PASS**.
- Full backend Release suite with real PostgreSQL enabled: **479/479 PASS**.
- Focused real PostgreSQL integration/race suite: **64/64 PASS**, **0 skipped**.
- Focused release smoke matrix: **6/6 PASS**:
  - checkpoint approval is audited/redacted and resumes the run to success;
  - durable cancellation of a waiting checkpoint;
  - due schedule produces one catch-up fire and one workflow run;
  - workflow experience is non-disclosing across tenants;
  - PostgreSQL workflow audit rows reject mutation;
  - artifact upload/download round-trip is workspace-scoped.
- Existing PostgreSQL coverage also proves:
  - concurrent workers claim a run only once;
  - stale workers are fenced by lease generation;
  - heartbeat requires the current fencing generation;
  - delayed work survives restart;
  - uncertain sensitive tool work becomes `OutcomeUnknown` and is not blindly replayed;
  - trigger fire processing is idempotent under concurrency;
  - run-as and approver membership races fail closed;
  - artifact quota/idempotency and crash recovery are concurrency-safe.

### EF migrations

- Clean database -> latest schema: **PASS**.
- Pre-Phase-5 schema (`20260925213117_Phase45AuditImmutability`) -> latest: **PASS**.
- Phase 5 rollback to Phase 4.5: **PASS**.
- Phase 5 reapply: **PASS**.
- Idempotent migration script generated and applied twice to the same clean database: **PASS**.
- Final migration history count: **19**.
- Phase 5 migrations verified:
  - `20260925233754_Phase5WorkflowsFoundation`
  - `20260926154306_Phase5CDurableRunner`
  - `20260926175544_Phase5DArtifactsStorage`

### Frontend

- `npm ci`: **PASS**, 0 reported vulnerabilities.
- ESLint: **PASS**.
- Vitest: **5 files / 11 tests PASS**.
- Next.js production build: **PASS**.
- Production routes generated: `/`, `/_not-found`, `/app`, `/login`, `/register`.

### AI runtime and benchmark harness

- Python AI tests: **5/5 PASS**.
- Benchmark harness dry-run: **PASS**.
- Dry-run evidence: dataset `rag-v2`, 13 documents, 18 cases; no provider activation performed.

### Docker

- API image build: **PASS**.
- AI image build: **PASS**.
- Isolated API/worker container startup against isolated PostgreSQL: **PASS**.
- Health endpoint after startup and restart: **PASS**.

### Crash/restart persistence smoke

A real isolated API/worker container and PostgreSQL database were used. A workflow run was persisted in a safe future-delay state, then the API/worker container was restarted.

Before restart:
- run: `Waiting`, current step `pause`, wait reason `Delay`, lease generation `1`;
- step: `WaitingForDelay`, attempt `1`.

After restart:
- API health returned successfully;
- run remained `Waiting | pause | Delay | 1`;
- step remained `WaitingForDelay | 1`;
- `ResumeAtUtc` remained in the future.

Result: **PASS — in-flight safe workflow state survives container restart without premature replay or state loss.**

## Manual/live smoke scope

The committed Phase 5 API surface at baseline `ef3f7dd` exposes workflow/run **read** APIs plus checkpoint decisions, trigger controls, and artifact APIs. It does **not** expose HTTP mutation endpoints for creating a workflow definition, creating/activating a version, or manually starting a run.

Therefore, the literal all-HTTP chain:

`create definition -> activate -> run -> checkpoint -> approve -> artifact -> success`

is not an executable public-API scenario in the committed Phase 5 contract. Phase 5H does not add new product features merely to manufacture that test.

The implemented runtime path is validated with real PostgreSQL through the focused smoke tests above:
- definition/version/run fixtures are persisted using the same domain and EF model;
- checkpoint approval resumes the durable runner to success;
- artifact storage has an independent real PostgreSQL round-trip;
- scheduler fire creates a deduplicated workflow run;
- cancellation, tenant isolation, and append-only audit behavior are independently exercised.

A fully authenticated HTTP smoke was not forced through the automation connector after the connector rejected a command that would handle temporary access tokens. The safety boundary was respected rather than bypassed.

## Independent review boundary

No separate human or Claude reviewer was available for this packet. Independent validation is provided by:
- clean worktree execution from the frozen baseline;
- real PostgreSQL integration and race tests;
- Docker builds/startup;
- format/diff validation;
- and the repository's GitHub Actions pipeline from a clean checkout.

GitHub Actions must be green before integration to `main`.

## Static integrity

- `dotnet format --verify-no-changes`: **PASS**.
- `git diff --check`: **PASS**.
- Staged Phase 5H secret scan: **PASS**.
- Only release-evidence/documentation changes are permitted in the Phase 5H commit.

## Release decision

**Phase 5 implementation is release-ready once GitHub Actions is green for this exact Phase 5H commit.**

No concurrent uncommitted work from the original developer worktree is part of this release candidate.
