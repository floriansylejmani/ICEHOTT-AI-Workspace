# Work Packet — Desktop Commander — Phase 3.6B Foundation Validation

## PostgreSQL proofs

- profile repository returns correct state-scoped profile;
- partial unique Active invariant survives migration;
- at most one Building profile if implemented as invariant;
- create profile-specific HNSW for a non-64 dimension test profile;
- verify pg_indexes expression dimension/profile predicate;
- reject >2000 HNSW vector dimensions;
- seed Ready chunks and prove Building coverage calculation;
- failed activation preserves current Active;
- successful activation retires A and activates B atomically;
- concurrent activation cannot leave two Active profiles.

## Provider contract proofs

- deterministic provider supports Document and Query purpose;
- provider registry rejects unknown provider;
- batch cap is honored;
- dimension mismatch is non-retryable;
- auth/config errors are non-retryable;
- transient/rate limit failures are retryable;
- OpenAI adapter mock HTTP request includes model and dimensions;
- no real API call occurs in normal tests.

## Evaluation proofs

- evidence is bound to dataset version + profile ID + model + dimensions + index version;
- evidence from a different profile cannot authorize activation;
- tenant leakage must be zero;
- benchmark runner does not auto-activate.

## Full gate

- backend Debug + Release;
- frontend tests/lint/build;
- AI tests;
- EF idempotent script;
- real PostgreSQL migration;
- Docker builds;
- /health + /ready;
- git diff --check;
- secret scan;
- no temp artifacts.
