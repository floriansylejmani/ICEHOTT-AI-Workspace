# Phase 4.5 — Work Packet C: Request Quotas, Body Bounds and Secret Redaction

Status: implementation on `phase-4.5-budgets-claude` (base `6eb83fc`, the reviewed Packet B
integration on `phase-4.5-lifecycle`), pending review. Not merged anywhere.
Spec: `docs/PHASE-4.5-SECURITY-DESIGN.md` § Budgets, secrets and audit.

Out of scope (other owners): cancellation, timeouts, leases, recovery and `OutcomeUnknown` (D);
database audit immutability and prompt-injection suite (E). Packet C does not change the
execution state machine, the audit tables or the Packet B policy contract.

## Contract decisions

Each decision is conservative (deny, fail closed) where the spec is silent.

**C1 — Quota shape.** A quota bounds how many executions one workspace may *admit* for one
tool per fixed, epoch-aligned window: `PermitLimit` per `WindowSeconds`. It is counted per
(workspace, tool) and shared by all members of the workspace. Limits come from server
configuration (`ToolQuotas`: default plus per-tool overrides, validated at startup:
1..1,000,000 permits, 1..86,400 s windows). Defaults: 60/min per tool, 20/min for
`workspace.audit-note.create`. Clients, prompts and tool arguments cannot influence them.
A per-workspace policy override is deliberately **not** added to the Packet B overlay in this
packet (that contract is frozen); it is listed for integration (F).

**C2 — Atomic admission.** Admission = one database transaction that (1) charges the quota with
a single conditional upsert on `tool_quota_counters` (PK `WorkspaceId, ToolName`):

```sql
INSERT … VALUES (ws, tool, window, 1)
ON CONFLICT (ws, tool) DO UPDATE SET
  Count = CASE WHEN excluded.window > c.window THEN 1 ELSE c.Count + 1 END,
  WindowStart = greatest-of(excluded.window, c.window)
WHERE excluded.window > c.window OR c.Count < limit
```

and (2) saves the execution row, its `Requested` audit event and the Packet B policy guard.
PostgreSQL locks the conflicting counter row and re-evaluates the `WHERE` on the latest
committed version, so concurrent charges serialise per (workspace, tool) and can never exceed
the limit. The counter is always locked first, so the transaction adds no lock-order cycle with
approvals or policy updates. Any non-`Saved` outcome rolls back the charge together with
everything else. A row carrying a later window (clock skew between nodes) is treated as the
current window, never reset backwards.

**C3 — Idempotency never double-charges.** The existing exact-retry lookup runs *before* the
charge, so a retry with the same key and arguments returns the original execution and is not
charged, even while the quota is exhausted. Races are closed by C2: a request that loses the
unique idempotency-index race rolls back its own charge and replays the winner; a request that
finds the quota exhausted re-checks the key and replays the winner if one was admitted
meanwhile (possibly the one that took the last permit). Same key with different arguments stays
`409 idempotency_conflict`. Nothing that is not admitted is charged: validation errors,
credential rejections, unknown tools, non-members, disabled tools, policy races.

**C4 — 429 contract.** `429 tool_quota_exceeded`, header `Retry-After: <seconds>` and body
`{ code, errors, retryAfterSeconds }`, where the value is the whole seconds (≥ 1, rounded up)
until the window closes. Authorization is decided first: an outsider gets
`404 workspace_not_found` and learns nothing about another workspace's quota. Approval-required
tools are charged when requested; approving or rejecting is never quota-limited. No currency
cost accounting is claimed: the two built-in tools have no priced cost; a cost budget needs an
explicit estimate/settlement interface for future priced adapters and is not implemented here.

**C5 — Body bounds before parsing.** `[RequestBodyLimit]` (MVC resource filter, runs after
authentication/authorization and before model binding) on the tool routes: 32 KiB for
`ToolsController` (2 × `MaxArgumentsBytes` covers JSON escaping, tool name and key; also bounds
the body-less approve/reject routes) and 4 KiB for `ToolPoliciesController`. A declared
`Content-Length` above the limit is rejected immediately; a body without one (chunked) is read
into a bounded buffer and rejected as soon as it passes the limit. Kestrel's per-request limit is
lowered to match where the host allows it. Response: `413 request_body_too_large`.

**C6 — Credential rejection at the API boundary.** After membership/policy checks and before
tool validation, persistence or charging, `ToolExecutionService.RequestAsync` rejects with
`400 credential_rejected`:
- any property, at any depth, whose name designates a credential (see policy below);
- any property name or string value matching a known credential format;
- an idempotency key matching a known credential format.
Errors carry the JSON path and the detector id, never the value; a credential-shaped property
name is reported at its parent's path so it is not echoed.

**C7 — Redaction of outputs.** Handler results are redacted (`SecretClassifier.RedactJson`)
before they are stored or returned: values under credential-named properties become
`[REDACTED]`, recognised formats inside any other string become `[REDACTED]`, everything else
is preserved. Every execution view (get/list/request/approve responses) additionally redacts
arguments, result and idempotency key, so rows stored before this packet cannot leak through
the API. Error codes/messages stored on executions stay fixed strings. Tool audit events carry
no payload (type, actor, time only).

**C8 — Exceptions and logs.** Handler exception details are never persisted or returned. A new
`IToolOperationalLog` receives the failure and logs: tool, execution and workspace ids, the
exception type chain, the **redacted** message of each exception and the stack trace. The
exception object is never passed to the logger (sinks would render its raw message, inner
exceptions and `Data`); arguments and results are never logged.

**C9 — Existing behaviour preserved.** Admission order, idempotency semantics, Packet B policy
guard and approval revalidation are unchanged; `ToolExecutionService` gains two constructor
dependencies (`ToolQuotaOptions`, `IToolOperationalLog`) and `IToolExecutionRepository` gains
`SaveAdmissionAsync` (used only for new admissions; state transitions still use
`SaveChangesAsync`).

## Credential policy and detection boundaries

`ICEHOTT.Application.Security.SecretClassifier` is the single policy used for rejection (C6)
and redaction (C7, C8).

**Credential field names** — normalised to lowercase ASCII letters (separators, digits, case
ignored). A name is a credential if it equals `auth`, `authorization`, `proxyauthorization`,
`cookie`, `setcookie`, `sessionid`, `sid`, `pwd`, `passwd`, `pin`, `otp`, `mfacode`, or contains
`password`, `passphrase`, `secret`, `token`, `apikey`, `accesskey`, `privatekey`, `signingkey`,
`encryptionkey`, `clientkey`, `credential`, `connectionstring`, `bearer`. This deliberately
over-matches (e.g. `tokenCount` is rejected); tools must not name ordinary arguments this way.

**Credential formats** (linear-time `NonBacktracking` regexes, 250 ms timeout, fail closed):
PEM private-key blocks; AWS access key ids (`AKIA/ASIA/ABIA/ACCA…`); GitHub classic
(`ghp_/gho_/ghu_/ghs_/ghr_`) and fine-grained (`github_pat_`) tokens; Anthropic (`sk-ant-`),
OpenAI (`sk-`, `sk-proj-`, …) and Stripe (`sk_/rk_ live/test`) keys; Slack (`xox?-`) tokens;
Google API keys (`AIza…`); JWTs (`eyJ….eyJ….sig`); `Bearer <token>`; credentials embedded in
URLs (`scheme://user:pass@`); Azure `AccountKey=`/`SharedAccessKey=`; and assignments such as
`password=…`, `secret: …`, `api_key=…`, `access_token=…` (value ≥ 4 characters).

**What is not detected (explicit limits).**
- Arbitrary free text can still contain a secret. Opaque random strings without a known prefix
  (a hex/base64 key, a password typed without `password=`, a proprietary token format, a
  secret split across fields or encoded) pass. No entropy heuristic is used: it would reject
  hashes, ids and idempotency keys. The classifier reduces exposure; it is not a guarantee.
- Basic-auth headers are not detected (the pattern collides with ordinary prose).
- Provider formats outside the list above are not detected. In particular the legacy
  Mailgun key shape `key-<32 hex>` is intentionally allowed: it is identical to the
  idempotency-key convention used by this API's clients and tests.
- Test credentials in the suite are assembled at runtime (`TestSecrets`) so the repository
  contains no literal token; GitHub push protection rejected an earlier revision that held a
  literal `key-<32 hex>` value.
- Known false positives are accepted: prose like `secret: tomorrow` or `password: changed`
  and any argument name containing `token`/`secret` are rejected (fail closed).
- Validation errors of tool schemas still echo *unknown argument names* that passed the
  credential scan.
- Operational logs contain redacted exception messages; an unrecognised secret inside an
  exception message reaches the (secured) log. Logs must stay access-controlled.
- Detection covers the tool API. Other surfaces (chat messages, knowledge documents, agent
  prompts) are not screened by this packet.

## API

| Code | HTTP | When |
| --- | --- | --- |
| `request_body_too_large` | 413 | Body above the route limit, checked before JSON parsing |
| `credential_rejected` | 400 | Credential-named field or recognised credential in arguments/key |
| `tool_quota_exceeded` | 429 | New admission beyond the (workspace, tool) window quota; `Retry-After` |

## Schema (migration `20260925204435_Phase45ToolQuotas`)

`tool_quota_counters` — PK (`WorkspaceId`, `ToolName` varchar(160)), `WindowStartUnixSeconds`
bigint, `Count` integer; `CK_tool_quota_counters_Count` (`Count >= 0`),
`CK_tool_quota_counters_WindowStartUnixSeconds` (`>= 0`); FK workspace `ON DELETE CASCADE`.
One row per (workspace, tool): the window rotates in place, so the table does not grow with
time. Down drops the table only.

## Evidence (local, 2026-09-25)

- Tests first: commit `c830987` adds the tests with compile-only stubs; 88 of 92 new tests fail
  (the 4 passing are regression guards), existing 285 pass.
- Implementation: Debug 377/377 and Release 377/377, each with PostgreSQL tests enabled
  (`ICEHOTT_POSTGRES_TEST_CONNECTION`, PostgreSQL 16.13 + pgvector 0.6.0) and without.
  Release build: 0 warnings, 0 errors. The 10 PostgreSQL tests (5 new) passed 15 consecutive
  runs.
- PostgreSQL migration proof on an empty database: full chain applies; check/FK/PK probes return
  23514/23514/23503/23505; the admission upsert returns 1,1,1,0 at limit 3, resets on a new
  window and never resets backwards; workspace delete cascades; rollback to
  `Phase45ToolPolicies` drops only `tool_quota_counters`; reapply succeeds; the idempotent
  script applies twice to a fresh database without errors.

## Open issues (for review)

1. **Policy-overlay quota.** The spec lets an Owner tighten the request budget per workspace;
   this packet keeps quotas in server configuration to avoid reopening the Packet B contract.
   Adding `maxRequestsPerWindow` to the overlay (min with configuration) is a small follow-up.
2. **Fixed windows** allow up to 2 × limit across a window boundary. A sliding window or token
   bucket needs more state per charge; not needed for the current tools.
3. **Rejected requests are not charged.** Floods of invalid requests are bounded by the body
   limit and authentication, not by this quota; a coarse per-user HTTP rate limiter could be
   added at integration.
4. **First-use policy anchor race** (Packet B open issue 1) still yields a retryable
   `409 policy_changed`; the quota charge of that loser is rolled back.
5. The SQLite suite cannot run truly parallel requests (one shared in-memory connection);
   parallel guarantees are proven by the PostgreSQL tests, which run only when
   `ICEHOTT_POSTGRES_TEST_CONNECTION` is set. CI must set it for them to count.
