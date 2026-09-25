# Phase 4 — Agent Tools & Execution Architecture

Status: implementation complete; release gate passed
Base: `main@706fb0a`
Branch: `phase-4-agent-tools-execution`

## Objective

Allow ICEHOTT agents to request workspace-scoped tools while keeping authorization, argument validation, approvals, persistence, and execution authority inside the ASP.NET Core trust boundary.

The AI runtime may propose a tool call in future orchestration work, but it never receives direct access to tool handlers, database credentials, workspace authorization, or approval state.

## Trust boundaries

```text
Untrusted / advisory
AI output, user text, retrieved knowledge
             |
             v
ASP.NET Core trusted boundary
workspace membership -> registry -> typed schema -> permission policy
                    -> idempotency -> approval state -> executor
                    -> persisted result + audit event
             |
             v
Tool implementation / workspace mutation
```

No model-provided claim such as "approved", "admin", "safe", or "already executed" is authoritative.

## Core domain

### Tool definition

Each registered tool declares:

- stable tool name;
- human-readable description;
- risk level;
- minimum workspace role allowed to request it;
- whether human approval is required;
- minimum approver role when approval is required;
- typed argument schema.

Phase 4 risk levels:

- `ReadOnly`: may execute immediately after membership, role, schema, and idempotency checks.
- `SensitiveWrite`: persists as `PendingApproval` and cannot execute until an authorized human approves it.

### Execution state machine

```text
request
  |
  +-- ReadOnly ------> Ready -> Running -> Succeeded
  |                              |
  |                              +------> Failed
  |
  +-- SensitiveWrite -> PendingApproval
                           |
                           +--> Rejected
                           |
                           +--> Ready -> Running -> Succeeded
                                           |
                                           +--> Failed
```

Terminal states cannot be restarted.

Approval invariants:

- approver must belong to the same workspace;
- approver must satisfy the tool's approval role;
- requester cannot approve their own sensitive execution (`403 self_approval_forbidden`); the requester cannot reject it either, so the decision always belongs to a different Admin/Owner;
- at approval time the requester's membership is re-resolved from the database: if the requester has been removed from the workspace or demoted below the tool's minimum requester role, approval returns `409 requester_no_longer_authorized`, nothing changes, and the execution can still be rejected;
- rejected executions cannot later be approved;
- an approval is bound to one execution ID inside one workspace and is consumed by the guarded PendingApproval → Ready transition; it cannot be reused or replayed;
- approval never comes from model output.

Approval executes the tool in the same request (`Ready → Running → Succeeded/Failed`); there is no separate `/execute` endpoint.

## Tenant and permission model

Every execution row carries `WorkspaceId`.

Every API operation first resolves the caller's membership in that exact workspace. Non-members receive the same not-found behavior used by the rest of ICEHOTT.

Execution reads are additionally filtered by tool permission: a caller may view their own execution, or executions for tools their current workspace role is allowed to request. This prevents Member-level users from reading Admin-only tool arguments and results.

Role ordering remains:

```text
Member < Admin < Owner
```

Initial built-in tools:

- `workspace.echo`
  - risk: ReadOnly
  - minimum requester: Member
  - approval: no
  - argument: `text` string, required, max 500

- `workspace.audit-note.create`
  - risk: SensitiveWrite
  - minimum requester: Admin
  - approval: yes
  - minimum approver: Admin
  - requester and approver must be different users
  - argument: `message` string, required, max 500

The second tool writes a workspace-scoped audit note and exists to prove the write/approval boundary without integrating email, payments, production mutation, or external SaaS actions yet.

## Typed argument validation

Tool arguments are JSON objects validated server-side by the registered tool.

Rules:

- unknown tool names are rejected;
- unknown argument properties are rejected (property names are case-sensitive), so `workspaceId`, `userId`, `requestedByUserId`, `approved` etc. can never be smuggled in as arguments;
- duplicate property names anywhere in the arguments object are rejected (JSON lookups resolve to the last duplicate while persistence stores all of them, so an earlier duplicate would otherwise be stored unvalidated);
- missing required fields are rejected;
- wrong JSON types are rejected;
- length limits apply to the raw string value, not the trimmed value (padding cannot bypass the bound);
- control characters other than tab/CR/LF are rejected (PostgreSQL rejects NUL in text columns);
- the canonical arguments JSON is capped at 16 KiB as a backstop for future tools;
- length/range limits are enforced before persistence/execution;
- tool code receives only arguments that passed its schema;
- raw model output is never executed as code, SQL, shell, or URLs.

## Idempotency

Clients must send an idempotency key.

Database uniqueness:

```text
(WorkspaceId, ToolName, IdempotencyKey)
```

Behavior:

- same key + same canonical argument hash returns the original execution;
- same key + different arguments returns `idempotency_conflict`;
- a terminal execution is never replayed by retrying the same key;
- the key is scoped to (workspace, tool), not to the requester: another member of the same workspace sending the same key and arguments receives the existing execution rather than creating a second side effect.

Concurrency:

- **Insert race.** Two concurrent requests with the same key both pass the pre-insert lookup; the unique index rejects the second insert. The loser discards its staged row and returns the winner's execution (or `idempotency_conflict` if its arguments differ). It never runs the handler.
- **Transition race.** `tool_executions.Status` is an EF optimistic concurrency token, so every transition is a compare-and-swap (`UPDATE ... WHERE "Status" = <status read>`). Two approvers (or approve vs. reject) racing on the same PendingApproval execution: exactly one wins, the other gets `409 invalid_state` with nothing committed, and the handler runs at most once. This needs no schema change.

Deeper replay protection (e.g. request signing, key expiry) remains Phase 4.5.

## Persistence and audit

`tool_executions` stores:

- workspace/requester/tool;
- risk and state;
- canonical arguments and SHA-256 argument hash;
- idempotency key;
- request/approval/start/completion timestamps;
- approver when present;
- persisted result JSON for success;
- bounded safe failure code/message for failure.

`tool_execution_audit_events` is append-only from the application path and records state transitions:

- Requested
- Approved
- Rejected
- Started
- Succeeded
- Failed

`workspace_audit_notes` proves a tenant-scoped write action and is linked to its originating execution.

Failure isolation:

- handler side effects, the `Succeeded` status and the `Succeeded` audit event commit in a single `SaveChanges`;
- if a handler throws, every change it staged is discarded before `Failed` is recorded, so a Failed execution has no side effects;
- only the fixed code `tool_execution_failed` and message `Tool execution failed.` are persisted; exception text (which may contain connection strings or other secrets) is never stored or returned.

No production secret should be accepted as a normal tool argument. Future secret-requiring tools must use server-side secret references.

## API surface

```text
GET  /api/workspaces/{workspaceId}/tools
POST /api/workspaces/{workspaceId}/tool-executions
GET  /api/workspaces/{workspaceId}/tool-executions
GET  /api/workspaces/{workspaceId}/tool-executions/{executionId}
POST /api/workspaces/{workspaceId}/tool-executions/{executionId}/approve
POST /api/workspaces/{workspaceId}/tool-executions/{executionId}/reject
```

## Phase 4 release blockers

- registry rejects duplicate tool names;
- tool arguments are validated before execution;
- workspace membership is required everywhere;
- role checks are server-side;
- sensitive write cannot execute before approval;
- self-approval is rejected;
- tenant-crossing execution lookup is rejected;
- idempotency replay returns the same execution;
- idempotency key reuse with different arguments is rejected;
- execution result is persisted;
- audit events are persisted;
- EF migration applies to PostgreSQL;
- backend regression suite passes;
- frontend and AI regression suites remain green;
- GitHub CI is green;
- phase is merged to `main` and feature branch is cleaned up.

## Validation evidence

Local release-gate evidence:

- backend Debug: 199/199 tests passed;
- backend Release: 199/199 tests passed;
- Phase 4 targeted tool/security suite: 86/86 passed;
- frontend: 8/8 tests passed, ESLint passed, production build passed;
- AI runtime: 5/5 pytest passed;
- EF migrations applied from an empty PostgreSQL/pgvector database through `20260925181737_Phase4AgentToolsExecution`;
- idempotent migration script generation passed;
- Docker images `icehott-api` and `icehott-ai` built successfully;
- live API + PostgreSQL smoke for `workspace.echo`: Succeeded with Requested/Started/Succeeded audit events;
- live PostgreSQL concurrency smoke for a sensitive write: two simultaneous approvals produced HTTP 200 + 409, exactly one workspace audit note, and exactly one Approved/Started/Succeeded audit-event sequence;
- staged secret scan: CLEAN; hardened-branch GitHub CI: backend/frontend/AI all passed, including PostgreSQL migration application and benchmark dry-run.

Additional security regressions now cover:

- Member users cannot read Admin-only sensitive execution details;
- requester permission is revalidated before a sensitive approval can execute;
- concurrent approve/reject transitions use optimistic concurrency so only one transition can win and a sensitive side effect runs at most once;
- handler writes staged before an exception are discarded before the execution is persisted as Failed;
- simultaneous requests with the same idempotency key converge on one execution instead of surfacing a 500 or duplicating side effects;
- idempotency keys are scoped independently per workspace;
- approval cannot cross workspace boundaries;
- duplicate JSON properties, control characters, wrong types, oversized/padded arguments, unknown properties, unknown tools, and invalid idempotency keys are rejected.

## Deferred to Phase 4.5

Phase 4.5 hardens this foundation with:

- configurable per-tool policy administration;
- stronger secret redaction/classification;
- recovery of executions left in `Running` if the process dies between the Ready → Running claim and completion (no lease/timeout yet; such rows never re-run, which is safe, but they need operator visibility);
- rate and cost budgets;
- cancellation and timeout policy;
- external high-risk tool adapters;
- prompt-injection-to-tool escalation suites;
- stronger append-only/immutable audit enforcement at the database boundary.

## Known limitations (Phase 4)

- Tool argument validation is implemented per tool (`IWorkspaceTool.ValidateArguments`); the advertised `ToolArgumentDefinition` list is descriptive and is not enforced generically, so the two can drift for future tools. A registry-level schema validator is a good Phase 4.5 candidate.
- Execution visibility is permission-aware: a user can always read their own execution, while other workspace members can read it only when their current role is allowed to request that tool. Member-level users therefore cannot read Admin-only sensitive execution arguments/results.
- There is no dedicated request-body size limit on `POST tool-executions` beyond the server default; oversized arguments are rejected after parsing.
- The API error code set is: `workspace_not_found`, `tool_not_found`, `execution_not_found` (404); `forbidden`, `self_approval_forbidden` (403); `idempotency_conflict`, `invalid_state`, `requester_no_longer_authorized` (409); `invalid_arguments`, `invalid_idempotency_key`, `approval_not_required` (400); `tool_execution_failed` (500, with the persisted Failed execution in the body).
