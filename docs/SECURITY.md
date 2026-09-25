# ICEHOTT Security Baseline

## Principles

- Least privilege
- Explicit tenant boundaries
- Human approval for sensitive actions
- No production secrets in source control
- Auditable agent and tool execution

## Phase 1 implemented controls

### Identity and sessions
- Passwords use ASP.NET Core `PasswordHasher`.
- Access JWTs are short lived.
- The frontend keeps access tokens in memory rather than localStorage.
- Refresh tokens are cryptographically random opaque values.
- Only refresh-token SHA-256 hashes are persisted.
- Refresh tokens rotate on every successful refresh.
- Replayed/revoked refresh sessions are rejected.
- Logout revokes the server-side refresh session.

### Browser boundary
- Refresh tokens are HttpOnly cookies.
- Production refresh cookies use `Secure`.
- Cross-site production cookies use `SameSite=None`.
- Refresh/logout require `X-ICEHOTT-CSRF: 1`.
- Credentialed CORS is restricted to configured origins.

### Authorization
- Workspace access is membership scoped on the server.
- Non-members receive 404 for workspace lookup.
- Admin workspace settings require Owner/Admin role.
- Frontend route guards are not security controls.

### Abuse resistance
- Authentication endpoints use a fixed-window per-IP rate limiter.
- Input DTOs enforce email/length constraints.
- Production startup rejects a missing/short JWT signing key.
- Production startup rejects missing CORS origins.

## Phase 3 implemented controls

### Knowledge isolation
- Knowledge listing, ingestion, search, and RAG retrieval require workspace membership.
- Vector rows carry `WorkspaceId` and retrieval filters both vector rows and documents by the authenticated workspace.
- Composite database foreign keys enforce matching `WorkspaceId` across conversations/messages/citations and documents/chunks/embeddings.
- Conversation citations are generated from server-side retrieval results, not from model-provided source labels.
- Failed indexing leaves the document marked `Failed` instead of presenting it as retrievable knowledge.

### Ingestion limits
- Knowledge title, source name, query, text, and upload sizes are bounded server-side.
- Uploaded filenames are reduced to their base filename before persistence.
- Uploads use an explicit extension allowlist: PDF, DOCX, TXT, MD, CSV, and JSON.
- PDF/DOCX/text extraction runs on the server; malformed supported files are rejected.
- The server enforces a 10 MB upload limit independently of browser checks.
- Embeddings are generated in bounded batches so large accepted documents cannot exceed the AI runtime request limit.

## Phase 3.5 implemented controls

### Queue and worker integrity
- Processing jobs are durable and workspace-bound in PostgreSQL.
- PostgreSQL leasing uses `FOR UPDATE SKIP LOCKED` so concurrent workers cannot lease the same job.
- Renew/complete/retry/fail operations require the active `LockedBy` worker ID.
- Expired leases can be recovered; retries use bounded backoff and maximum attempts.
- Reindexing is idempotent while a job is already Queued or Processing.

### Retrieved-content safety
- Retrieved chunks are treated as untrusted content.
- Common instruction-override, prompt-exfiltration, and unsafe-tool/bypass patterns are removed before RAG context reaches the agent runtime.
- Backend-derived citations remain independent of model-generated source labels.
- This deterministic filter is defense in depth and does not replace trusted prompt boundaries or server-side tool authorization.

## Phase 3.6B implemented controls

### Semantic provider isolation
- Retrieval resolves only the Active embedding profile; Building/Retired/Failed profiles cannot serve normal RAG requests.
- Building-profile generation reuses stable chunk IDs and never runs the serving document ReplaceChunks path.
- External embedding-provider batches are grouped by WorkspaceId so content from different tenants is never mixed in one provider request.
- Vector storage and retrieval remain workspace-filtered and profile-filtered.

### Provider configuration and secrets
- Production-provider API keys remain server-side configuration only.
- OpenAI credentials are mapped from `OPENAI_API_KEY` to the API container and are never exposed to the browser.
- Readiness verifies that the Active provider is configured for the exact profile without making a paid live embedding call.
- Missing provider credentials make the API not-ready instead of silently falling back to an incompatible vector space.

### Promotion and activation
- Candidate providers remain Building until evaluation evidence passes.
- Promotion evidence is bound to dataset/profile/provider/model/dimensions/index version.
- Tenant leakage must be zero.
- Production promotion requires offline semantic evidence when configured.
- PostgreSQL activation uses SERIALIZABLE isolation, table locks, final coverage/index/evidence checks, and an atomic Active/Retired state swap.
- Retired vectors and indexes are not automatically deleted during activation, preserving rollback options.

## Phase 4 implemented controls

### Tool trust boundary
- Tool execution authority remains in ASP.NET Core; AI/model output cannot directly invoke handlers.
- Every tool request is resolved through a server-owned registry with typed argument validation.
- Unknown arguments and wrong JSON types are rejected before persistence/execution.
- Tool definitions declare stable risk level, minimum requester role, approval requirement, and minimum approver role.

### Workspace authorization and approvals
- Every tool list/request/read/approve/reject path resolves membership in the addressed workspace.
- Non-members receive the same workspace-not-found behavior used elsewhere.
- Read-only tools may execute only after membership, role, schema, and idempotency checks.
- Sensitive writes persist as `PendingApproval`.
- Sensitive writes require an authorized Admin/Owner approver who is different from the requester.
- Self-approval and under-privileged approval are rejected server-side.
- Requester membership and minimum requester role are revalidated at approval time so a revoked or demoted request cannot later execute.
- Tool execution reads are permission-filtered; callers cannot inspect arguments/results for tools above their current workspace role unless they are the original requester.

### Replay and audit baseline
- Tool requests require an idempotency key.
- The database enforces uniqueness across `WorkspaceId + ToolName + IdempotencyKey`.
- Canonical JSON arguments are SHA-256 hashed so the same key cannot silently authorize different arguments.
- Execution state, timestamps, approver, bounded failure metadata, and result JSON are persisted.
- Requested/Approved/Rejected/Started/Succeeded/Failed audit events are persisted.
- Audit events and workspace audit notes use composite execution/workspace foreign keys so tenant ownership cannot be crossed in persistence.

## Required controls for later phases

- Malware scanning and deeper file-signature inspection for production uploads
- Stronger prompt-injection classifiers / model-aware defenses for adversarial corpora
- Admin-configurable per-tool policies beyond the frozen built-in registry
- Secret storage through deployment platforms and secret-reference-only tool arguments
- Database-enforced append-only audit immutability and retention policy
- Security headers and HTTPS at deployment edge
- External high-risk tool adapters such as email sending, destructive writes, and production mutations
- Model/prompt/tool version traceability
- Per-tool rate/cost budgets, cancellation deadlines, and concurrent replay hardening

## AI-specific rules

- Treat retrieved content as untrusted data, never as system instructions.
- Separate model output from executable tool arguments.
- Validate every tool schema server-side.
- Require approval for sensitive external actions.
- Record model, prompt version, tool calls, and final outcome for each agent run.
