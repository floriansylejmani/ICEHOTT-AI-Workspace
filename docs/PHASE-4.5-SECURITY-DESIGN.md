# Phase 4.5 — Security, Approvals & Audit Hardening

Status: implementation integrated on `phase-4.5-finalization`; local release gate passed, pending remote CI and merge review.
Base: `main@3c1b832`. Phase 4 is merged and validated there; Phase 4.5 remains off `main` until the final gate is approved.

## Intent and scope

Harden the existing workspace tool execution path so a compromised prompt, stale approval, failed process, or over-budget client cannot produce an unauthorized or untraceable side effect. Preserve Phase 4's server-owned authorization, typed arguments, idempotency, one-execution approval, and tenant isolation.

This phase delivers policy controls, safe audit evidence, bounded execution, operational recovery, and adversarial tests. External SaaS write adapters are deferred until their concrete security contract and credentials are specified; no generic URL/shell executor is introduced.

## Trust invariants

- The API resolves caller, workspace membership and tool policy from trusted server state at each request and approval.
- Prompt text, retrieved content, and AI-supplied tool arguments cannot set role, approval, policy, execution state or secret references.
- A requester cannot approve or reject their own sensitive execution. A different current Admin/Owner approves one execution within the workspace.
- A policy change or requester demotion before approval must not allow execution under obsolete privileges.
- A retry, concurrent approval, timeout or process restart must not repeat a write whose outcome is uncertain.
- No secret or raw exception enters responses, persisted arguments/results, normal telemetry or audit events.
- Tenant crossing always fails before execution lookup or disclosure.

## Policy model

Keep the built-in tool registry as the authoritative schema and handler binding. Add a versioned, server-managed per-workspace policy overlay keyed by workspace and tool name. The effective policy can only tighten a built-in definition: disable a tool, raise requester/approver minimum role, require approval, reduce argument/result limits, tighten timeout and request budget. It cannot downgrade a SensitiveWrite to ReadOnly, remove mandatory approval, or elevate a caller. Missing overlay means built-in defaults.

Only Owner can alter an overlay through a dedicated API; log actor, prior and new policy versions. Reject unknown tools, invalid bounds and stale update versions. Snapshot effective policy version and relevant limits on each execution; before approval re-resolve current policy and re-check both requester and approver under the stricter of snapshot/current requirements. If disabled, reject approval with a stable conflict code. Existing terminal executions remain readable under permission-aware visibility.

## Budgets, secrets and audit

Apply per-workspace, per-tool admission quotas before recording a new execution; repeated identical idempotency keys return the existing row without another quota debit. Persist quota consumption in a transaction protected by database uniqueness/concurrency; return a stable 429 with retry metadata. Cost budgets require an explicit tool cost estimate and settled actual cost, so do not claim currency accounting for the two existing built-ins. Define a bounded request count now and reserve the cost interface for future priced adapters.

Reject designated secret-shaped fields and known credential patterns in arguments at the API boundary; enforce maximum request body size before JSON parsing. Redact sensitive values in result and error serialization, structured logs and audit metadata. Keep exception details only in secured operational logging with no raw tool inputs. Do not attempt a false guarantee that arbitrary free text can never contain a secret: document classification limits and test representative tokens.

The current application audit table is append-only by convention. Add database enforcement that rejects UPDATE/DELETE on tool audit rows for the application role, and verify migration rollback semantics. Any privileged operator maintenance path must be separately documented. Each event includes execution, workspace, actor (or system actor), timestamp and transition; never persist unredacted arguments in the audit payload.

## Cancellation, timeouts and recovery

Introduce `Cancelled` and `TimedOut` terminal states and a bounded execution deadline. Cancellation before Running can prevent a handler; cancellation during Running is cooperative and cannot imply rollback after an external side effect. Handler interfaces receive a cancellation token; timeout failures use stable safe codes. Never auto-retry a write merely because a lease expires.

A Running row records lease owner and expiry. A recovery worker marks expired rows `OutcomeUnknown` with an audit event and exposes them to Owner/operator review; it never re-executes them automatically. Only a tool with a proven idempotent external contract may later support explicit recovery after reconciliation. Re-check compare-and-swap transitions for approval, cancellation, timeout and worker races.

## Adversarial acceptance tests

- Retrieved documents claiming \"system approval\" or embedding tool-call JSON do not grant authorization.
- A Member cannot request Admin tools, inspect someone else's sensitive execution, or approve writes.
- Cross-workspace IDs and forged workspace fields fail without leaking execution details.
- A policy downgrade attempt fails; a stricter policy activated while approval is pending is enforced.
- Duplicate requests, two approvers, approve versus cancel, and expired leases produce at most one write.
- Oversized bodies, credential-looking fields, malicious JSON and secret-bearing handler exceptions cannot leak secrets.
- Quota exhaustion returns 429; an exact idempotent retry remains the original execution.
- Audit UPDATE/DELETE fails with the application DB role; append succeeds.

## Work ownership and integration order

| Packet | Owner | Branch/worktree | Deliverable |
| --- | --- | --- | --- |
| A — design and security contracts | ChatGPT | `phase-4.5-security-architecture` | This spec, interface freeze, review gate |
| B — policy overlay and approval revalidation | Claude Code | `phase-4.5-policy-claude` from reviewed A commit | Schema/migration, API, versioned effective policy, tests |
| C — quota, body bounds and redaction | Claude Code after B review | separate `phase-4.5-budgets-claude` | Transactional request quotas, safe inputs/outputs, tests |
| D — lifecycle and recovery | ChatGPT + Desktop Commander after B interface freeze | separate `phase-4.5-lifecycle` | Cancellation, timeout, OutcomeUnknown, lease worker, tests |
| E — immutable audit and abuse suite | Claude Code after D contracts | separate `phase-4.5-audit-claude` | DB enforcement, prompt-injection tests, migration proof |
| F — integration and release gate | ChatGPT + Desktop Commander | integration branch | Review diffs, reconcile migrations, full verification |

Claude must use a dedicated git worktree and commit each packet separately; never edit `main` or the ChatGPT worktree. Do not start B until A is reviewed. In particular B and D may touch `ToolExecutionService`, so merge B and freeze its contract before D starts. C may proceed independently only after B's policy contract is stable. No one should run concurrent EF migration generation against the same checkout.

## Release evidence

Require Debug/Release backend suites, frontend lint/tests/build, AI tests, PostgreSQL migration apply and idempotent script, database audit immutability check, live tenant/approval/quota/recovery smoke, Docker builds and CI. Review generated migration and snapshot, staged secrets, git diff and API error contracts. No Phase 4.5 PASS or merge claim until all evidence exists.
