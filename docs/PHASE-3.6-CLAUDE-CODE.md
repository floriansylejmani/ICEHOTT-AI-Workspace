# Work Packet — Claude Code — Phase 3.6

## Role
Implementation agent. Work only after architecture freeze and research acceptance criteria are available.

## Baseline
main at 2d48f9e.

## Branching rule
Use a dedicated worktree/branch. Never modify or push directly to main.

## Scope
- provider-neutral semantic embedding configuration;
- explicit model/dimension/index-version metadata;
- safe vector-version compatibility;
- deterministic local provider preserved for tests/dev;
- semantic provider adapter selected by lead architecture;
- optional semantic/model reranker behind interface;
- evaluation dataset schema + deterministic eval runner;
- telemetry/model traceability;
- tests and docs.

## Required invariants
- workspace tenant isolation unchanged;
- no secrets in source/logs;
- no incompatible vectors mixed in retrieval;
- ingestion queue/retry/lease semantics preserved;
- citations remain server-derived;
- deterministic fallback remains testable;
- no quality claims without eval evidence.

## Output
Commit(s) on the Phase 3.6 worktree only. Do not merge or push main. Provide:
- file list;
- architecture notes;
- migration notes;
- tests added;
- commands executed;
- known limitations.
