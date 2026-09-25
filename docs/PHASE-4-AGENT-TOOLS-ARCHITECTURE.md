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
- requester cannot approve their own sensitive execution;
- requester membership and minimum requester role are revalidated at approval time;
- rejected executions cannot later be approved;
- approval never comes from model output.

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
- missing required fields are rejected;
- wrong JSON types are rejected;
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
- a terminal execution is never replayed by retrying the same key.

Concurrent-race hardening beyond the database uniqueness constraint is part of Phase 4.5 replay hardening.

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

- backend Debug: 127/127 tests passed;
- backend Release: 127/127 tests passed;
- Phase 4 targeted tool suite: 14/14 passed;
- frontend: 8/8 tests passed, ESLint passed, production build passed;
- AI runtime: 5/5 pytest passed;
- EF migrations applied from an empty PostgreSQL/pgvector database through `20260925181737_Phase4AgentToolsExecution`;
- idempotent migration script generation passed;
- Docker images `icehott-api` and `icehott-ai` built successfully;
- live API + PostgreSQL smoke for `workspace.echo`: Succeeded with Requested/Started/Succeeded audit events;
- staged secret scan: CLEAN; hardened-branch GitHub CI: backend/frontend/AI all passed, including PostgreSQL migration application and benchmark dry-run.

Additional security regressions now cover:

- Member users cannot read Admin-only sensitive execution details;
- requester permission is revalidated before a sensitive approval can execute;
- idempotency keys are scoped independently per workspace;
- approval cannot cross workspace boundaries;
- unknown tools, wrong types, oversized arguments, unknown properties, and invalid idempotency keys are rejected.

## Deferred to Phase 4.5

Phase 4.5 hardens this foundation with:

- configurable per-tool policy administration;
- stronger secret redaction/classification;
- concurrent replay race handling;
- rate and cost budgets;
- cancellation and timeout policy;
- external high-risk tool adapters;
- prompt-injection-to-tool escalation suites;
- stronger append-only/immutable audit enforcement at the database boundary.
