# Phase 4.5 Packet B Independent Review

Review date: 2026-09-25. Claude source commit: `fcd76fb7a31755a06b34b5fb98b3f11a9eec3993` based on `06416a6`. Integrated only into `phase-4.5-lifecycle` as cherry-pick `6d6098c`; `main` remains unchanged.

## Decision

**PASS for Packet B integration**, subject to the Phase 4.5 release blockers below. The policy overlay and approval revalidation match the written Packet B contract. This is not a Phase 4.5 release PASS.

- D2 approved: approver floor is at least the requester floor; an Owner request needs a different Owner.
- D6 approved as a linearization rule: approval commits against the policy version it observed. A later policy update does not retroactively revoke an already committed approval. Any stronger requirement to stop a handler between approval and execution needs a separate design.
- First concurrent use may return retryable `409 policy_changed` while materializing version 0. This fails closed; track as availability issue, not an authorization bypass.

## Evidence independently checked

- Clean Claude worktree at `fcd76fb`; `git diff --check 06416a6 fcd76fb` clean.
- Release solution build: 0 warnings, 0 errors.
- Claude branch: Debug 239/239; Release 239/239.
- Combined lifecycle + policy branch: Debug 285/285; Release 285/285.
- Migration inspected: backfill precedes check constraints; `CK_tool_executions_PolicyApprover` explicitly rejects NULL; Down removes both policy tables and five execution columns.
- Existing local PostgreSQL verification log `p45b-pgverify2.log` records migration apply, constraint probes, guarded UPDATE 1/0, rollback, reapply, and 5/5 PostgreSQL tests. This reviewer inspected the log and migration but did not recreate a fresh database during this review. The final release gate must run PostgreSQL verification against the combined migration chain.
- No Packet C/D/E implementation was included in Claude's commit.

## Open security and integration work

1. A requester or approver membership change can race between the approval re-check and commit. The policy version is guarded, membership is not. Add a membership row/version guard or equivalent serializable authorization decision before the Phase 4.5 security release; test demotion/removal races against approval and policy update.
2. Implement Packet C quotas, bounded request bodies and redaction.
3. Implement Packet D deadlines, leases, cancellation and recovery. The domain states alone do not make execution bounded.
4. Implement Packet E audit immutability and prompt-injection escalation tests.
5. Run the complete migration/rollback and live concurrency gate after all branches are integrated.
