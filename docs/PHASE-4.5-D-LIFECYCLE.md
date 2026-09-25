# Phase 4.5 Packet D — Tool Lifecycle Progress

Status: implementation in `phase-4.5-lifecycle`; not a Phase 4.5 release approval.

## Implemented behavior

- Each new Running execution records a lease owner, a 30-second deadline and a lease expiry 45 seconds after start in the same guarded transition as the Started audit event.
- The API passes a linked cancellation token to the handler. A cooperative handler that exceeds 30 seconds ends as `TimedOut` for ReadOnly tools, or `OutcomeUnknown` for SensitiveWrite tools. The response uses HTTP 504 `tool_timeout`. A sensitive write's external effect may already have happened.
- `POST /api/workspaces/{workspaceId}/tool-executions/{executionId}/cancel` permits the requester or a current Owner to cancel PendingApproval/Ready. It records one Cancelled audit event; repeated cancellation is 409. Non-members and other Members receive not-found behavior.
- A Running cancellation request returns 409 `execution_running`; it does not change status or falsely claim to undo a possible side effect. HTTP request aborts are passed to the handler; for a SensitiveWrite abort, the stored state is `OutcomeUnknown`.
- A scoped recovery service queries at most 100 expired Running rows per pass, including old rows with no lease. The hosted worker checks every 15 seconds. It marks each row `OutcomeUnknown` and records a system audit event using the existing Status concurrency token. It never calls the tool handler or replays a write.
- Execution reads expose the terminal state and audit trail under existing workspace visibility checks. Recovery logs aggregate counts only.

## Verification

- Domain transition and lease validation tests, HTTP cancellation/tenant tests, a slow ReadOnly handler timeout test, a slow SensitiveWrite timeout test, and expired/legacy recovery tests pass.
- Combined backend Debug: 291/291; Release: 291/291 (before the final additional Running cancellation assertion, whose targeted test passed).
- EF pending-model check: no changes; idempotent script generated.
- Isolated PostgreSQL/pgvector 16: Packet B migration followed by lifecycle lease migration applied; all three lease columns verified; rollback removed them; reapplication and repeat update succeeded. The isolated container was removed.

## Limits to close before Phase 4.5 PASS

1. The `cancel` endpoint does not signal a Running handler. A durable, cross-process cancellation request and handler polling/acknowledgement design is required if that behavior is promised. Current response makes no such promise.
2. A handler that ignores cancellation may run past its deadline; recovery marks the expired row `OutcomeUnknown`. The concurrency guard prevents its later database commit, but cannot undo an external side effect.
3. The current built-in timeout is 30 seconds; a server-managed per-tool timeout policy and cost limit belong to the remaining Phase 4.5 integration work.
4. The live PostgreSQL recovery transition and simultaneous completion/recovery race still require an integration smoke. SQLite tests cover the transition and repeated pass.
5. Packet B's requester/approver membership race remains a final security gate; Packet C and E are not integrated yet.
