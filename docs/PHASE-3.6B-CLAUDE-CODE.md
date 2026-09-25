# Work Packet — Claude Code — Phase 3.6B Foundation

## Baseline
Branch/worktree should start from commit d75d08d.

## Read first
- docs/PHASE-3.6-ARCHITECTURE-FREEZE.md
- docs/PHASE-3.6A-SEMANTIC-FOUNDATION.md
- docs/PHASE-3.6B-PROVIDER-RESEARCH.md
- docs/PHASE-3.6B-ARCHITECTURE.md

## Role
Implementation agent. Do not merge or push main.

## Implement in dependency order

1. Profile repository and separate Serving/Building resolvers.
2. Refactor embedding provider contract to accept profile + EmbeddingPurpose and expose capabilities.
3. Update local deterministic provider/runtime adapter and all callers.
4. Add profile-aware HNSW index provisioner for current vector/HNSW/cosine path.
5. Add building-profile vector coverage/completeness service.
6. Add structured deterministic evaluation evidence storage/model.
7. Add transactional activation service.
8. Add OpenAI embedding adapter behind the provider registry with mock HTTP tests only.
9. Add benchmark runner that never auto-activates a profile.
10. Update readiness/telemetry/docs/tests.

## Hard invariants

- retrieval uses Active only;
- build/reindex uses Building only;
- Building is never silently served;
- current HNSW path rejects dimensions > 2000;
- provider response dimensions must exactly match profile;
- provider purpose Query/Document must be explicit;
- no API keys in source/logs;
- activation is transactional and preserves old Active on failure;
- no retired-index deletion during activation;
- no semantic quality claims without benchmark evidence.

## OpenAI adapter scope

Implement adapter architecture and mock tests. Do not make a live call and do not require a real API key for normal tests.

Target structural profile for tests:
- provider: openai
- model: text-embedding-3-small
- dimensions: 1536
- distance: cosine

Do not activate this profile automatically.

## Deliverable

Return:
- commits on the implementation branch only;
- exact files changed;
- tests added;
- migration/index SQL notes;
- commands run;
- limitations;
- explicit statement that no live provider benchmark was run unless credentials were separately authorized.
