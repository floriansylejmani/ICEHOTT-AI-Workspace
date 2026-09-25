# Phase 4.5 — Work Packet B: Per-Tool Policy Overlay and Approval Revalidation

Status: implementation on `phase-4.5-policy-claude` (base `06416a6`), pending ChatGPT review.
Spec: `docs/PHASE-4.5-SECURITY-DESIGN.md` § Policy model.

Out of scope (other packets): quotas/budgets and body limits (C), redaction (C),
cancellation/timeouts/recovery/`OutcomeUnknown` (D), database audit immutability (E).

## Contract decisions (recorded before implementation)

The spec leaves several security-relevant points open. These are the decisions
this packet implements; each one is conservative (deny/tighten) where the spec
is silent.

**D1 — Overlay shape and "only tighten".**
A workspace policy for a tool has: `enabled`, `minimumRequesterRole?`,
`minimumApproverRole?`, `requiresApproval`, `maxArgumentLength?`. A value that is
weaker than the built-in definition is **rejected** (`400 policy_downgrade_rejected`),
never silently clamped, so an Owner is never misled about what is enforced:

- `minimumRequesterRole` below the built-in requester role;
- `minimumApproverRole` below `max(Admin, built-in approver role)`;
- `requiresApproval: false` for a tool whose built-in definition requires approval
  (every `SensitiveWrite` today);
- any attempt to send `riskLevel` (or any other unknown property) — the body is
  parsed strictly; risk level is not a policy field and can never change.

`maxArgumentLength` must be `1..built-in max` and applies to every string argument
(`min(built-in MaxLength, policy)`); for a tool without bounded string arguments it
must be null (`400 invalid_policy`).

**D2 — Approver floor.** Effective approver role when approval is required is
`max(Admin, built-in approver, policy approver, effective requester role)`.
Someone who may not request a tool may not authorise it either. Consequence: if an
Owner raises a tool's requester role to Owner, approval needs a *different* Owner.

**D3 — "Version".** Each (workspace, tool) has an integer version. No row means
built-in defaults, version 0. Every successful Owner update increments the version
by exactly 1 and appends a policy audit event (actor, previous version, new version,
previous and new policy JSON, timestamp). Updates carry `expectedVersion`; a
mismatch returns `409 policy_version_conflict` with the current version. The
version is also an EF optimistic concurrency token, so two concurrent updates with
the same `expectedVersion` cannot both commit.

**D4 — Execution snapshot.** Every execution stores the effective policy it was
admitted under: `PolicyVersion`, requester role, approver role (nullable), requires
approval, max argument length (nullable). Rows created before this migration are
backfilled to version 0 with the built-in values of their tool.

**D5 — Revalidation at approval (the core invariant).** Approval re-reads the
current policy and current memberships and applies the **stricter** of snapshot
and current: roles = max, max argument length = min, enabled = current.
- tool disabled now → `409 tool_disabled`, no state change;
- approver below the stricter approver role → `403 forbidden`;
- requester removed or below the stricter requester role →
  `409 requester_no_longer_authorized`;
- stored arguments exceed the stricter length → `409 policy_limit_exceeded`;
- the self-approval ban is unchanged.
Loosening a policy while an execution is pending never loosens that execution;
tightening always applies.

**D6 — Race between approval and a policy change.** The approval commit
(PendingApproval → Ready) is made conditional on the policy row still holding the
version that was evaluated: the same `SaveChanges` re-asserts
`UPDATE tool_policies … WHERE "Version" = <read version>`; if no row exists a
default version-0 row is inserted, which collides on the primary key with a
concurrent first policy insert. A concurrent Owner change therefore either commits
before the approval (approval fails with `409 policy_changed`, retry re-evaluates)
or after it (the approval was decided under the prior version, linearised first).
The same guard protects immediate execution of non-approval tools at request time.
Materialised version-0 rows are equivalent to "no overlay" and create no policy audit
event. A membership change racing an approval is not covered by this guard (same as
Phase 4) — see open issues.

**D7 — Request-time enforcement.** Requests use the current effective policy:
disabled → `409 tool_disabled`; role below effective requester role → `403 forbidden`;
string argument over effective length → `400 invalid_arguments`; policy-required
approval turns a ReadOnly tool into `PendingApproval`.

**D8 — Reject.** Rejecting needs the stricter approver role and stays allowed while
a tool is disabled, so stale pending executions can always be cleaned up.

**D9 — Visibility and listing.** `GET tools` lists only enabled tools the caller may
request, showing effective values and `policyVersion`. Execution visibility for
non-requesters uses `max(snapshot requester role, current effective requester role)`.

**D10 — Policy API authorisation.** All policy routes resolve membership from the
database first (non-member → `404 workspace_not_found`). Read: Admin and Owner.
Write: Owner only (`403 forbidden` otherwise). Unknown tool → `404 tool_not_found`.

## API

```text
GET /api/workspaces/{workspaceId}/tool-policies                 Admin, Owner
GET /api/workspaces/{workspaceId}/tool-policies/{toolName}      Admin, Owner
PUT /api/workspaces/{workspaceId}/tool-policies/{toolName}      Owner
GET /api/workspaces/{workspaceId}/tool-policies/{toolName}/audit Owner
```

PUT body (all properties required except the nullable ones; unknown properties rejected):

```json
{
  "expectedVersion": 0,
  "enabled": true,
  "minimumRequesterRole": "Admin",
  "minimumApproverRole": null,
  "requiresApproval": true,
  "maxArgumentLength": 200
}
```

Response: the stored overlay and the resulting effective policy.

## Schema (migration `Phase45ToolPolicies`)

- `tool_policies` — PK (`WorkspaceId`, `ToolName`), `Version` (concurrency token),
  `Enabled`, `MinimumRequesterRole?`, `MinimumApproverRole?`, `RequiresApproval`,
  `MaxArgumentLength?`, `UpdatedByUserId?`, `UpdatedAtUtc`; FK workspace (cascade),
  FK user (restrict); check constraints on role values and length bounds.
- `tool_policy_audit_events` — `Id`, `WorkspaceId`, `ToolName`, `ActorUserId`,
  `PreviousVersion`, `NewVersion`, `PreviousPolicyJson?`, `NewPolicyJson`,
  `OccurredAtUtc`; index (`WorkspaceId`, `ToolName`, `NewVersion`) unique.
- `tool_executions` — adds the D4 snapshot columns.

## Error codes added

| Code | HTTP |
| --- | --- |
| `tool_disabled` | 409 |
| `policy_changed` | 409 |
| `policy_limit_exceeded` | 409 |
| `policy_version_conflict` | 409 |
| `policy_downgrade_rejected` | 400 |
| `invalid_policy` | 400 |

## Implementation notes

- Stricter-of is `EffectiveToolPolicy.StricterOf` (Domain); the only-tighten
  rules and effective computation are `ToolPolicyEvaluator` (Application, pure).
- Both repositories share one `SaveChanges` classifier (`ToolPersistence`) that maps
  a policy `Version` token mismatch, or a primary-key collision on the version-0
  anchor row, to `ToolPersistenceOutcome.PolicyConflict`; nothing is committed.
- `CK_tool_executions_PolicyApprover` uses an explicit `IS NOT NULL`. Without it,
  `TRUE AND NULL BETWEEN 2 AND 3` evaluates to NULL and PostgreSQL CHECK accepts
  NULL, so an approval-required snapshot without an approver role would have been
  storable. Found by the PostgreSQL constraint probe; covered in the migration proof.
- Migration backfill: `PolicyMinimumRequesterRole` defaults to Member (1); rows of
  `workspace.audit-note.create` and any row still `PendingApproval` are set to
  requester Admin / approver Admin / requires approval before the check constraints
  are added. Down-migration drops the two tables and the five snapshot columns.

## Open issues (for review)

1. **Spurious `409 policy_changed` on first concurrent use.** While no overlay row
   exists, each guarded decision inserts the version-0 anchor row; two concurrent
   first decisions for the same (workspace, tool) collide on the primary key and
   the loser gets a retryable `policy_changed`. Fails closed; a retry succeeds.
   Could be removed by seeding version-0 rows when a workspace is created.
2. **Approval then policy change.** The guard linearises an approval that commits
   before an Owner change as "decided under the prior version": the handler still
   runs after the change. This matches D6 but should be confirmed by the architect.
3. **Membership changes racing an approval** are not serialised (unchanged from
   Phase 4): a demotion committing between the requester re-check and the approval
   commit is not detected. A membership version/row guard would close it.
4. **`maxArgumentLength` is one limit for all string arguments** of a tool. Per-argument
   limits and result/timeout/budget limits belong to packets C and D.
5. **Materialised version-0 rows** appear in `GET tool-policies` as version 0 with
   defaults (identical to "no overlay"); they create no policy audit event.
6. Policy audit events are append-only by convention only; database enforcement is
   packet E.
