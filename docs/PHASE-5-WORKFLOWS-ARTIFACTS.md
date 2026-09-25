# Phase 5 — Workflows & Artifacts Architecture Freeze

Date: 2026-09-26
Branch: `phase-5-workflows-architecture`
Base: `76c657d`

## Status

**FROZEN / PASS for Phase 5A architecture. Runtime implementation may begin only
from a new Phase 5B branch after this document is integrated into `main`.**

The user explicitly chose to continue without Claude Code. The architecture
review was therefore completed in ChatGPT against the current repository,
Phase 4.5 invariants, PostgreSQL worker/lease patterns and release evidence.

The Phase 0–4.5 system has passed local end-to-end smoke testing across
authentication, workspace isolation, chat persistence, RAG, citations, tool
execution, approval, idempotency, quota, cancellation, timeout/recovery
semantics, refresh-token rotation and restart persistence.

The live development database currently has only
`local-deterministic-64-v1` as the Active embedding profile. A production
semantic-profile activation is therefore deferred and is not a Phase 5
prerequisite.

## Objective

Add durable workspace-scoped workflows that can span multiple steps, survive
process restarts, pause for human decisions, retry safely, create durable
artifacts, and run manually or from bounded schedules/triggers.

Phase 5 must preserve every Phase 4.5 authorization and audit invariant. A
workflow is orchestration; it is never an alternate authorization path around
tools, workspace membership, approvals, quotas, or secret policy.

## Non-goals

- No arbitrary user-supplied code execution.
- No browser automation or external connector framework in Phase 5 core.
- No unbounded cron fan-out.
- No direct model authority over approvals or workflow permissions.
- No automatic replay of a sensitive step after an uncertain outcome.
- No production object-storage vendor lock-in; Phase 6 owns production storage
  deployment choices.
## Core domain model

### WorkflowDefinition

Workspace-owned logical workflow identity.

Required fields:

- `Id`
- `WorkspaceId`
- `Name`
- `Description`
- `Status`: `Draft | Active | Archived`
- `MinimumRunRole`: `Member | Admin | Owner`
- `CreatedByUserId`
- `CreatedAtUtc`
- `UpdatedAtUtc`

A definition is mutable only while Draft. Activating a changed workflow creates
a new immutable version rather than mutating a version already used by runs.

### WorkflowVersion

Immutable executable snapshot.

Required fields:

- `Id`
- `WorkflowDefinitionId`
- `WorkspaceId`
- `VersionNumber`
- `DefinitionJson`
- `DefinitionHash` (SHA-256 of canonical definition)
- `Status`: `Draft | Active | Retired`
- `CreatedByUserId`
- `CreatedAtUtc`
- `ActivatedAtUtc` nullable
- `RetiredAtUtc` nullable

A run always pins one exact WorkflowVersion. Activation is transactional: the
new version becomes `Active` and any previously Active version for the same
workflow becomes `Retired` in the same transaction. Existing runs keep their
pinned version. Existing triggers also remain pinned to their configured version;
activating a new version never silently retargets a trigger.

### WorkflowRun

Required fields:

- `Id`
- `WorkspaceId`
- `WorkflowDefinitionId`
- `WorkflowVersionId`
- `RequestedByUserId`
- `RunAsUserId`
- `IdempotencyKey`
- `Status`
- `CurrentStepKey` nullable
- `CreatedAtUtc`
- `StartedAtUtc` nullable
- `CompletedAtUtc` nullable
- `LeaseOwner` nullable
- `LeaseExpiresAtUtc` nullable
- `LeaseGeneration` monotonic integer used as a fencing token
- `WaitReason` nullable: `Checkpoint | Delay | RetryBackoff`
- `ResumeAtUtc` nullable
- `ErrorCode` / safe `ErrorMessage` nullable

Run states:

`Queued -> Running -> Waiting -> Running -> Succeeded`

`Waiting` always has a persisted `WaitReason`; delay/retry waits also have
`ResumeAtUtc`. A checkpoint wait is resumed only by a committed checkpoint
decision, never by time alone.

Terminal alternatives:

`Failed | Cancelled | OutcomeUnknown`

`OutcomeUnknown` is terminal until an explicit human reconciliation action is
implemented. It is never auto-replayed.

### WorkflowStepRun

One persisted record per step attempt.

Required fields:

- `Id`
- `WorkflowRunId`
- `WorkspaceId`
- `StepKey`
- `Attempt`
- `StepType`
- `Status`
- `InputJson`
- `OutputJson` nullable and redacted where required
- `ToolExecutionId` nullable
- `StartedAtUtc` / `CompletedAtUtc` nullable
- `NextAttemptAtUtc` nullable
- `ErrorCode` / safe `ErrorMessage` nullable

Step states:

`Pending -> Ready -> Running -> Succeeded`

Additional states:

`WaitingForCheckpoint | WaitingForDelay | WaitingForRetry | Failed | Cancelled |
Skipped | OutcomeUnknown`

Attempt numbers are monotonic and unique per run + step key.
### WorkflowCheckpoint

Human decision boundary.

Required fields:

- `Id`
- `WorkspaceId`
- `WorkflowRunId`
- `StepRunId`
- `RequestedByUserId`
- `MinimumApproverRole`
- `RequiresDifferentApprover`
- `Status`: `Pending | Approved | Rejected | Expired`
- `DecidedByUserId` nullable
- `CreatedAtUtc`
- `DecidedAtUtc` nullable
- `Reason` nullable, bounded and redacted

Checkpoint approval is server-authoritative. Where `RequiresDifferentApprover`
is true, the checkpoint requester cannot approve their own checkpoint. Approve
and reject are first-writer-wins transitions committed with current approver
membership/role revalidation; concurrent or stale decisions return a stable
invalid-state result and never overwrite the winning decision.

A sensitive tool step still goes through the Phase 4/4.5 tool-approval boundary;
a workflow checkpoint does not replace tool approval.

### Artifact

Workspace-owned durable output metadata.

Required fields:

- `Id`
- `WorkspaceId`
- `CreatedByUserId`
- `WorkflowRunId` nullable
- `StepRunId` nullable
- `FileName`
- `ContentType`
- `SizeBytes`
- `Sha256`
- `StorageKey`
- `Status`: `Pending | Ready | Failed | Deleted`
- `CreatedAtUtc`
- `DeletedAtUtc` nullable

Artifact bytes are accessed only through `IArtifactStore`. Domain/Application
must not depend on local filesystem, S3, Azure Blob, or another concrete store.

Development may use a local provider. Phase 6 selects production object storage.

Artifact persistence is two-phase. Bytes are streamed to a generated staging key
while size and SHA-256 are computed server-side. Metadata may be persisted as
`Pending`, but an artifact cannot become `Ready` until the store confirms the
complete durable object and the computed checksum/size match persisted metadata.
A bounded orphan-staging sweeper removes abandoned temporary objects. Deletion is
first represented as a durable tombstone/status transition, then physical byte
removal is retried idempotently; clients never choose storage keys.

### WorkflowTrigger

Required fields:

- `Id`
- `WorkspaceId`
- `WorkflowDefinitionId`
- `WorkflowVersionId`
- `Type`: initially `Schedule`
- `ScheduleExpression`
- `TimeZoneId`
- `RunAsUserId`
- `Enabled`
- `NextRunAtUtc`
- `LastRunAtUtc` nullable
- `CreatedByUserId`
- `CreatedAtUtc`

### WorkflowTriggerFire

One durable row per logical scheduled occurrence.

Required fields:

- `Id`
- `WorkspaceId`
- `TriggerId`
- `WorkflowVersionId`
- `ScheduledForUtc`
- `FireKey`
- `Status`: `Claimed | RunCreated | Skipped | Failed`
- `WorkflowRunId` nullable
- `CreatedAtUtc`
- `CompletedAtUtc` nullable

`FireKey` is server-derived and unique per logical occurrence. Run creation and
transition of the fire to `RunCreated` happen transactionally so a scheduler
crash cannot create two runs for one occurrence.

Phase 5 scheduling is bounded and server-generated. Minimum frequency and
maximum active triggers per workspace are configurable quotas.

A scheduled fire runs as `RunAsUserId`. The scheduler revalidates that user's
current workspace membership and `MinimumRunRole` before each fire. A removed or
demoted run-as user causes the fire to fail closed and the trigger to be disabled
with an audit event rather than repeatedly running with stale authority.

Schedule evaluation is timezone-aware but persists `NextRunAtUtc` as the source
of truth. Phase 5 supports a bounded cron-style schedule with a configured minimum
interval. DST/misfire policy is deterministic: an ambiguous local time fires once,
a skipped local time advances to the next valid occurrence, and downtime may
produce at most one catch-up fire per trigger. Catch-up storms are forbidden.

## Workflow definition format

The canonical definition is versioned JSON. Each step has a stable `key` and a
typed `type`.

Initial step types:

- `tool` — invoke a registered workspace tool through ToolExecutionService;
- `checkpoint` — pause for a human decision;
- `artifact` — materialize/transform a bounded artifact through an approved
  application handler;
- `delay` — wait until a persisted UTC instant;
- `condition` — deterministic branch on prior structured outputs.

No arbitrary script/eval step exists.

Each step declares:

- dependencies;
- typed input bindings;
- timeout;
- retry policy;
- optional output/artifact binding.

The server validates the whole graph before activation:

- step keys are unique;
- dependencies exist;
- graph is acyclic;
- entry step is deterministic;
- timeout/retry bounds are valid;
- referenced tools exist;
- role requirements are compatible;
- artifact limits are bounded.

Canonicalization is server-owned. Duplicate JSON property names are rejected
before graph validation. `DefinitionHash` is computed from one deterministic
canonical serialization; clients do not submit or choose the hash.

Phase 5 has explicit graph budgets: maximum definition bytes, maximum step count,
maximum dependency edges, maximum binding depth, maximum per-step input/output
bytes and maximum delay duration. Exact values are configuration with validated
upper bounds and are covered by request-body limits.

The first Phase 5 runner executes at most one step per workflow run at a time.
Parallel branches are deferred. Conditions may choose deterministic successor
paths but may not use script/eval or an unbounded expression language.

## Durable runner

A hosted worker claims queued/runnable work with PostgreSQL transactional leases,
following the proven KnowledgeIngestionWorker pattern.

Rules:

1. claim with `FOR UPDATE SKIP LOCKED` or equivalent repository primitive;
2. persist lease owner + expiry before execution;
3. heartbeat only while work is owned;
4. commit each state transition independently;
5. on restart, reclaim expired safe work;
6. never auto-replay a sensitive external step whose outcome may be uncertain;
7. cap attempts, backoff and runtime;
8. no in-memory state may be required to resume a run.

Redis is optional for wake-up/coordination optimisation, not the source of
truth. PostgreSQL remains the durable authority.

### Lease fencing and stale-worker protection

Lease expiry alone is not sufficient. Every successful claim increments
`LeaseGeneration`. Every heartbeat and state transition performed by a worker
must include `RunId + LeaseOwner + LeaseGeneration` in the update predicate and
must affect exactly one row. A worker holding an older generation is fenced out
even if it resumes after a pause or network partition.

Claim, generation increment and transition to the claimed runnable state occur
in one transaction. Recovery never relies on wall-clock observations made before
the claim transaction commits.

The scheduler uses the same fencing principle for trigger claims. Every logical
fire has a unique deterministic fire key derived from `TriggerId + ScheduledForUtc`;
a unique database constraint prevents duplicate run creation across scheduler
instances.

## Tool-step execution identity

A workflow never calls a tool handler directly. A `tool` step calls
`ToolExecutionService` using `RunAsUserId` as the requester. Manual runs set
`RunAsUserId` to the authenticated requester. Scheduled runs use the trigger's
persisted run-as user after fresh membership/role revalidation.

Before every new step starts, the runner revalidates `RunAsUserId` membership and
the workflow's `MinimumRunRole`. If authority was removed or reduced, the run
fails closed with a safe authorization error and no new step is started. This is
in addition to each downstream tool/checkpoint/artifact operation enforcing its
own current authorization boundary.

The workflow engine derives a stable tool idempotency key from the workflow run
and stable step identity. Worker retry/recovery reuses that key; it must not
manufacture a fresh tool execution for the same logical step. The tool's own role,
policy, quota, secret and approval rules remain authoritative.

If a tool execution reaches `OutcomeUnknown`, the step and workflow run become
`OutcomeUnknown`. The workflow cannot infer success, retry the side effect or
advance to a dependent step automatically.

## Retry semantics

Auto-retry is allowed only when the step contract is explicitly retry-safe.

- deterministic/read-only/internal steps: bounded retries allowed;
- idempotent tool step: rely on stable tool idempotency key derived from
  workflow run + step + attempt policy;
- approval-required sensitive write: no automatic re-request after an uncertain
  execution;
- `OutcomeUnknown`: stop the workflow and require reconciliation.

Retry policy fields are bounded: max attempts, initial delay, maximum delay and
backoff multiplier.

Cancellation is durable. Cancelling a queued/waiting run is terminal immediately.
For a running internal safe step, the runner requests cooperative cancellation and
records the terminal result. For a running sensitive external tool, cancellation
must use the Phase 4.5 tool lifecycle semantics; if the external outcome cannot be
proven, the run finishes as `OutcomeUnknown`, not `Cancelled`.

## API surface

All endpoints are under `/api/workspaces/{workspaceId}` and require workspace
membership.

### Definitions

- `POST /workflows`
- `GET /workflows`
- `GET /workflows/{workflowId}`
- `POST /workflows/{workflowId}/versions`
- `POST /workflows/{workflowId}/versions/{versionId}/activate`
- `POST /workflows/{workflowId}/archive`

Creating/changing/activating definitions requires Admin or Owner.

### Runs

- `POST /workflow-runs`
- `GET /workflow-runs`
- `GET /workflow-runs/{runId}`
- `POST /workflow-runs/{runId}/cancel`
- `POST /workflow-runs/{runId}/retry` only for explicitly retryable failed
  states

A run request includes a client idempotency key. Manual execution must revalidate
current membership and `MinimumRunRole`; a client cannot submit `RunAsUserId` for
another user. List endpoints are paginated with server-enforced maximum limits.

### Checkpoints

- `GET /workflow-runs/{runId}/checkpoints`
- `POST /workflow-runs/{runId}/checkpoints/{checkpointId}/approve`
- `POST /workflow-runs/{runId}/checkpoints/{checkpointId}/reject`

### Artifacts

- `POST /artifacts/upload`
- `GET /artifacts`
- `GET /artifacts/{artifactId}`
- `GET /artifacts/{artifactId}/content`
- `DELETE /artifacts/{artifactId}`

Artifact content is streamed only after membership validation. Storage keys are
never accepted from the client and never exposed as direct filesystem paths.

### Triggers

- `POST /workflows/{workflowId}/triggers`
- `GET /workflows/{workflowId}/triggers`
- `POST /workflows/{workflowId}/triggers/{triggerId}/enable`
- `POST /workflows/{workflowId}/triggers/{triggerId}/disable`

Trigger administration requires Admin or Owner. The API never accepts an
arbitrary run-as identity: `RunAsUserId` is server-bound to the authorized actor
(or to a separately reviewed delegation feature not included in Phase 5 core).
At fire time the scheduler revalidates this identity again.

## Security invariants

Release blockers:

1. Every workflow, run, step, checkpoint, artifact and trigger is workspace
   scoped in persistence and queries.
2. Non-members receive 404-style non-disclosure for workspace-owned resources.
3. A workflow cannot bypass ToolExecutionService, tool policy, quota, approval,
   secret filtering or audit.
4. Definition activation is role-gated and versioned; runs never execute mutable
   definitions.
5. Checkpoint approval is revalidated transactionally at commit time.
6. User-provided filenames never become trusted storage paths.
7. Artifact upload has type, size and checksum bounds.
8. Workflow inputs/outputs are bounded before JSON parsing/persistence.
9. Secrets are rejected/redacted using the Phase 4.5 boundary.
10. Run and step idempotency is tenant-scoped.
11. Scheduler claims are at-most-once per trigger fire key.
12. A stale lease cannot cause the same sensitive side effect to run twice.
13. Workflow audit events are append-only in PostgreSQL.
14. Prompt/retrieved content cannot create, activate, approve or schedule a
    workflow without server-side user authority.
15. Error text exposed/persisted to normal records never contains raw exception
    secrets.

## Audit model

Add `workflow_audit_events` as an append-only workspace-bound table.

Required fields:

- `Id`
- `WorkspaceId`
- `WorkflowDefinitionId` nullable
- `WorkflowRunId` nullable
- `WorkflowStepRunId` nullable
- `WorkflowTriggerId` nullable
- `ActorUserId` nullable for system-generated events
- `EventType`
- `DetailJson` nullable, bounded and redacted
- `CreatedAtUtc`

Event examples:

- DefinitionCreated
- VersionCreated
- VersionActivated
- RunRequested
- RunStarted
- StepStarted
- StepSucceeded
- StepFailed
- CheckpointRequested
- CheckpointApproved
- CheckpointRejected
- ArtifactCreated
- RetryScheduled
- RunCancelled
- RunSucceeded
- RunFailed
- OutcomeUnknown
- TriggerFired

Database immutability enforcement must match the Phase 4.5 tool audit pattern.
Audit foreign keys must not rely on cascade-delete paths that would defeat or
conflict with append-only enforcement.

## Required database constraints and indexes

At minimum the persistence design must enforce in PostgreSQL:

- unique `(WorkflowDefinitionId, VersionNumber)`;
- at most one Active version per workflow definition through a partial unique
  index or equivalent transactional invariant;
- unique `(WorkspaceId, WorkflowDefinitionId, IdempotencyKey)` for manual and
  API-created workflow runs;
- unique `(WorkflowRunId, StepKey, Attempt)`;
- at most one checkpoint per `WorkflowStepRunId`;
- unique `(TriggerId, ScheduledForUtc)` and unique `FireKey` for trigger fires;
- indexes for workspace-scoped list queries and runnable/expired lease scans;
- check constraints for non-negative attempt/generation counters and bounded
  terminal timestamps where practical;
- restrictive relationships for immutable audit history;
- no foreign-key path may allow a resource from one workspace to reference a
  version, run, step, artifact or trigger owned by another workspace.

Every transition that relies on membership, role, version status, lease fencing
or checkpoint authority must be committed atomically with the data it protects.
Application pre-checks alone are not sufficient for race-sensitive transitions.

## Observability

Use `ICEHOTT.Workflow` ActivitySource/Meter.

Record bounded metadata only:

- workspace/run/step IDs;
- workflow version/hash;
- step type and attempt;
- queue/lease latency;
- execution duration;
- retry count;
- checkpoint wait duration;
- artifact bytes/count;
- trigger delay;
- terminal outcome.

Do not record raw workflow input, artifact content, secrets, or full model
prompts in standard telemetry.

## Phase 5 implementation packets

### 5A — Architecture freeze
Owner/reviewer: GPT-5.6 Sol / ChatGPT

Deliver this document, threat model, API/state contracts and acceptance tests
before runtime implementation. Independent Claude review was waived by the user
when Claude Code quota became unavailable.

### 5B — Domain + persistence
Owner: GPT-5.6 Sol / ChatGPT
Validation: Remote Desktop Commander

Implement entities, configurations, migrations, repositories, indexes and
append-only workflow audit.

### 5C — Durable runner + recovery
Implement leases, claims, state transitions, retries, cancellation, restart
recovery and OutcomeUnknown rules.

### 5D — Artifacts
Implement `IArtifactStore`, development storage provider, upload/download,
checksums, quotas, workspace authorization and lifecycle.

### 5E — Human checkpoints + security
Implement checkpoint approval/rejection, membership-race protection, separation
of duty and adversarial tests.

### 5F — Scheduled triggers
Implement bounded schedules, trigger-fire idempotency, leases and duplicate
prevention.

### 5G — Frontend
Add workflow list/detail/run timeline, checkpoint actions, artifact browser and
trigger controls. No authorization rule may exist only in the frontend.

### 5H — Final hardening and release
Independent review, PostgreSQL race tests, crash/restart smoke, Docker/CI and
release evidence.

## Required test matrix

Before merge, automated coverage must prove:

- graph validation rejects cycles, unknown dependencies and invalid step types;
- workspace isolation for every new endpoint/table;
- definition versions are immutable once used/activated;
- duplicate run idempotency keys do not duplicate work;
- concurrent workers cannot claim one step twice;
- expired safe work can resume after restart;
- sensitive uncertain work becomes OutcomeUnknown and is not replayed;
- retries respect max attempts/backoff;
- cancellation is persisted and survives restart;
- checkpoint self-approval/role bypass is rejected where separation is required;
- membership demotion/removal racing checkpoint approval fails closed;
- artifacts reject traversal, oversized payloads and invalid content metadata;
- artifact checksums are verified;
- cross-tenant artifact reads return non-disclosing 404;
- schedule fires are idempotent and quota bounded;
- workflow audit UPDATE/DELETE is rejected by PostgreSQL;
- prompt injection cannot activate/approve/schedule a workflow;
- tool steps still obey Phase 4.5 policy, quota, idempotency and audit rules.

## Phase 5 merge gate

Required evidence:

- backend Debug + Release tests pass;
- real PostgreSQL workflow concurrency/race tests pass;
- EF idempotent migration script passes;
- migrations apply from a clean database and from current main schema;
- migration rollback/reapply evidence for Phase 5 schema;
- frontend lint/tests/production build pass;
- AI tests pass;
- Docker API/AI builds pass;
- full stack restart preserves in-flight safe workflow state;
- live manual smoke: create definition -> activate -> run -> checkpoint -> approve
  -> artifact -> success;
- live cancellation smoke;
- live schedule-fire smoke;
- tenant isolation smoke with two users;
- append-only workflow audit smoke;
- git diff/check/secrets scan clean;
- CI green before integration.

## Architecture decision

Phase 5 starts with PostgreSQL as the durable orchestration authority and an
`IArtifactStore` abstraction for bytes. It reuses the existing workspace,
authorization, tool-execution, audit and hosted-worker patterns instead of
introducing a separate workflow platform.

This architecture is intentionally conservative: correctness, restart safety,
tenant isolation and auditable human control are prioritised over visual
workflow editing or maximum throughput.
