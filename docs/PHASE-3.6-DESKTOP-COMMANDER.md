# Work Packet — Desktop Commander — Phase 3.6 Validation

## Role
Independent local verification and release gate after Claude implementation.

## Required checks
- inspect full git diff from baseline;
- dotnet build/test Debug and Release;
- frontend test/lint/build;
- FastAPI tests;
- EF migrations script --idempotent;
- apply migrations to real PostgreSQL/pgvector;
- inspect vector schema/index/version constraints;
- Docker build and startup;
- API /health and /ready;
- AI /ready;
- live ingest -> queue -> semantic/local embedding -> Ready;
- query/retrieval/citations smoke;
- provider outage/fallback smoke;
- cross-workspace retrieval rejection;
- incompatible vector/index-version rejection;
- evaluation runner results;
- git diff --check;
- no secrets or temporary files;
- only after lead approval: commit/merge/push.

## Evidence format
PASS/FAIL with exact command/result counts and any limitations.
