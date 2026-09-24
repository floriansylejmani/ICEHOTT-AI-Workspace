# ICEHOTT Multi-Agent Engineering Roadmap

## Operating model

ICEHOTT uses a controlled multi-agent workflow. GitHub `main` is the source of truth.

- **GPT-5.6 Sol / ChatGPT** — Lead Architect and final reviewer. Owns phase boundaries, interfaces, security/tenant invariants, merge decisions, and release claims.
- **Claude Code** — Implementation and independent code-review agent. Works in a dedicated git worktree/branch. It does not push directly to `main`.
- **ChatGPT Work** — Deep research and independent audit. Owns current-provider research, benchmark methodology, evaluation design, threat-model research, and long-form comparison work. It does not modify the local repository directly unless explicitly assigned through Work.
- **Desktop Commander** — Local execution and verification layer. Owns actual filesystem inspection, builds, tests, Docker, PostgreSQL, migrations, logs, smoke tests, git diff verification, commit, and push after lead approval.
- **GitHub** — Shared source of truth, history, CI, and final integration point.

## Engineering rules

1. One owner per work packet.
2. Claude Code works on a worktree/branch for implementation tasks.
3. Research output must be converted into explicit acceptance criteria before code changes.
4. No agent may claim PASS without executable evidence.
5. No direct push to `main` until Desktop Commander runs the merge gate and GPT-5.6 Sol reviews the diff/results.
6. Existing user or agent changes are preserved; never reset unknown work.
7. Tenant isolation, authorization, migrations, and auditability are release blockers.
8. Semantic/RAG quality claims require measured evaluation, not intuition.

# Phase 3.6 — Production Semantic RAG & Evaluation

## Objective

Replace the deterministic 64-dimensional development embedding baseline with a production-capable, provider-neutral semantic path while preserving the existing Phase 3.5 queue, tenant, retry, retrieval, and citation guarantees. Add a versioned RAG evaluation system so provider/model changes can be promoted only with evidence.

## Work Packet A — Architecture & provider contract
Owner: GPT-5.6 Sol
Reviewer: Claude Code

- Define semantic embedding provider contract.
- Define provider/model/version metadata.
- Define configurable dimensions and pgvector compatibility strategy.
- Define migration/reindex policy when dimensions or models change.
- Define degradation behavior when semantic provider is unavailable.
- Freeze interfaces before implementation.

## Work Packet B — Current provider/model research
Owner: ChatGPT Work
Reviewer: GPT-5.6 Sol

Research current production options and return a dated comparison:
- embedding models/providers;
- vector dimensions and limits;
- batch limits;
- latency/cost characteristics;
- data-retention/privacy controls;
- regional/enterprise constraints;
- reranking options;
- SDK/API stability;
- operational fallback options.

Deliverable: research memo with sources and a neutral comparison. No provider selection is accepted until architecture and evaluation requirements are matched.

## Work Packet C — Semantic embedding implementation
Owner: Claude Code
Validation: Desktop Commander

- Implement provider-neutral options/config.
- Preserve current local deterministic provider as explicit development/test provider.
- Add one production semantic provider only after Work Packet B review.
- Add model/dimension validation.
- Add timeout/retry classification.
- Persist provider/model/index-version metadata required for reproducibility.
- Never expose secrets in source or logs.

## Work Packet D — Vector index versioning and reindex
Owner: Claude Code
Architecture review: GPT-5.6 Sol
Validation: Desktop Commander

- Make embedding dimensions/configuration explicit.
- Design safe pgvector schema strategy for provider/model changes.
- Add index versioning.
- Prevent mixing incompatible vectors.
- Add controlled workspace/document reindex path.
- Preserve tenant constraints.
- Add migration + rollback evidence.

## Work Packet E — Reranking
Owner: Claude Code
Research: ChatGPT Work

- Keep `IRagReranker` boundary.
- Add optional semantic/model reranker behind a provider interface.
- Preserve deterministic reranker as fallback/test path.
- Bound candidate count, latency, and cost.
- Trace reranker provider/model/version.
- Never promote it without evaluation evidence.

## Work Packet F — Versioned RAG evaluation
Owner: GPT-5.6 Sol for spec
Implementation: Claude Code
Dataset/research audit: ChatGPT Work
Execution: Desktop Commander

Dataset records must include:
- workspace-safe synthetic/evaluation document set;
- query;
- expected relevant document/chunk IDs or relevance labels;
- expected citation/source facts;
- optional answer reference/criteria;
- dataset version.

Deterministic metrics:
- retrieval hit-rate / recall@K;
- precision@K where labels allow;
- citation source correctness;
- tenant leakage = 0;
- unsafe retrieved-content filtering;
- latency;
- error/fallback rate.

Offline/model-based metrics:
- groundedness;
- answer relevance;
- faithfulness;
- context precision/recall;
- citation correctness.

CI must run deterministic gates. Model-based evals remain an explicit offline/promotion gate unless made stable and cost-bounded.

## Work Packet G — Observability / traceability
Owner: Claude Code
Validation: Desktop Commander

Trace:
- embedding provider/model/dimensions/index version;
- reranker provider/model/version;
- retrieval candidate count and final K;
- filtered chunk count/reason;
- indexing/retrieval/rerank latency;
- fallback path;
- evaluation dataset/version and result summary.

No raw secrets or sensitive document contents in normal telemetry.

## Phase 3.6 merge gate
Owner: Desktop Commander + GPT-5.6 Sol

Required:
- backend Debug and Release tests pass;
- frontend tests/lint/build pass;
- AI tests pass;
- EF idempotent migration script passes;
- migrations apply to real PostgreSQL/pgvector;
- Docker builds pass;
- API/AI readiness pass;
- semantic provider smoke test when credentials are explicitly configured;
- deterministic fallback smoke works without production credentials;
- tenant isolation tests remain green;
- version-mismatch vectors cannot be mixed;
- evaluation dataset and deterministic eval runner pass;
- docs match actual behavior;
- git diff clean and reviewed.

# Phase 4 — Agent Tools & Execution

## Objective

Allow ICEHOTT agents to use tools safely inside a workspace.

### GPT-5.6 Sol
- tool execution architecture;
- permission model;
- approval state machine;
- audit schema;
- trusted/untrusted boundary design.

### ChatGPT Work
- current agent-tool safety research;
- human approval patterns;
- tool permission / least-privilege research;
- failure and abuse scenarios.

### Claude Code
- tool registry;
- typed tool schemas;
- workspace-scoped permissions;
- execution records;
- idempotency keys;
- approval-required execution state;
- tool-result persistence;
- unit/integration tests.

### Desktop Commander
- migration verification;
- malicious/invalid tool argument tests;
- tenant-crossing tests;
- approval bypass tests;
- Docker/live tool sandbox smoke;
- merge gate.

# Phase 4.5 — Security, Approvals & Audit Hardening

- explicit per-tool policies;
- high-risk action approvals;
- immutable/auditable execution trail;
- secret redaction;
- replay protection;
- rate/cost limits;
- cancellation/timeouts;
- prompt-injection-to-tool escalation tests.

# Phase 5 — Workflows & Artifacts

- durable multi-step workflows;
- resumable state;
- artifacts/files;
- workflow retries;
- human checkpoints;
- scheduled/triggered execution;
- workspace audit history.

# Phase 6 — Production Deployment & SRE

- production environment separation;
- secrets manager;
- managed database/vector strategy;
- object storage;
- OpenTelemetry collector/export;
- dashboards/alerts;
- backup/restore;
- migration rollout/rollback;
- load/performance tests;
- disaster recovery runbook.

## Immediate execution order

1. Phase 3.6 architecture freeze.
2. ChatGPT Work research packet.
3. Claude Code implementation worktree.
4. Desktop Commander validation.
5. GPT-5.6 Sol final review.
6. Merge/push only after PASS.
