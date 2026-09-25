# ICEHOTT API Contract — Phases 1–4

Base path: `/api`

## Authentication model

ICEHOTT uses two credentials with separate responsibilities:

- **Access token**: short-lived JWT returned in the response body and held in frontend memory.
- **Refresh token**: opaque random token stored only in an HttpOnly cookie. Only a SHA-256 hash is stored in the database.

Refresh tokens rotate on every successful refresh. Replaying a revoked refresh token returns `401`.

Cookie-authenticated mutation endpoints require:

```http
X-ICEHOTT-CSRF: 1
```

This custom header forces a browser CORS preflight and is accepted only from configured frontend origins.

## Auth endpoints

### POST `/api/auth/register`

Request:

```json
{
  "email": "user@example.com",
  "displayName": "Example User",
  "password": "at-least-12-characters"
}
```

Returns user identity, access token, access-token expiry, and sets the refresh cookie.

### POST `/api/auth/login`

Uses email/password and returns the same public auth response as registration.

### POST `/api/auth/refresh`

Requires the HttpOnly refresh cookie and `X-ICEHOTT-CSRF: 1`.

Rotates the refresh session and returns a new access token.

### POST `/api/auth/logout`

Requires `X-ICEHOTT-CSRF: 1`.

Revokes the active refresh session and deletes the refresh cookie.

### GET `/api/auth/me`

Requires:

```http
Authorization: Bearer <access-token>
```

Returns the current authenticated user.

## Workspace endpoints

All workspace endpoints require a valid access token.

### POST `/api/workspaces`

Creates a workspace. The creator is persisted as `Owner`.

### GET `/api/workspaces`

Lists only workspaces for which the current user has membership.

### GET `/api/workspaces/{workspaceId}`

Returns the workspace only when the caller is a member.

For a non-member, the API returns `404` rather than exposing whether another tenant's workspace exists.

### GET `/api/workspaces/{workspaceId}/settings`

Admin boundary:
- `Owner` → allowed
- `Admin` → allowed
- `Member` → `403`
- no membership → `404`

## Agent runtime endpoints

All agent endpoints require a valid access token and workspace membership.

### GET `/api/workspaces/{workspaceId}/conversations`

Returns the latest workspace conversations, ordered by most recent activity.

### GET `/api/workspaces/{workspaceId}/conversations/{conversationId}`

Returns one workspace-scoped conversation and its persisted message history.

A caller without workspace membership receives `404`. A conversation from another workspace is never returned.

### POST `/api/workspaces/{workspaceId}/conversations/chat`

Request:

```json
{
  "conversationId": null,
  "content": "Hello ICEHOTT"
}
```

Omit the conversation ID by sending `null` to start a new conversation. Reuse the returned ID for the next turn.

The API persists the user message, embeds the prompt, retrieves relevant workspace chunks from pgvector, sends workspace-scoped history plus retrieved context to FastAPI, persists the assistant message and source citations, and returns both messages plus runtime metadata.

Representative agent errors:
- `message_required`
- `message_too_long`
- `conversation_not_found`
- `workspace_not_found`
- `ai_runtime_unavailable`
- `knowledge_unavailable`

## Knowledge / RAG endpoints

All knowledge endpoints require a valid access token and membership in the workspace.

### GET `/api/workspaces/{workspaceId}/knowledge/documents`

Lists only documents owned by the requested workspace, including indexing status and chunk count.

### POST `/api/workspaces/{workspaceId}/knowledge/documents`

Request:

```json
{
  "title": "Support Policy",
  "sourceName": "support-policy.md",
  "content": "Workspace knowledge text..."
}
```

The API validates the request, persists the document as `Queued`, creates or reuses its durable processing job, and returns `202 Accepted`. A background worker later leases the job, chunks the content, requests embeddings in batches, stores document/chunk metadata in PostgreSQL, and writes vectors to pgvector. The document becomes `Ready` only after indexing succeeds.

### POST `/api/workspaces/{workspaceId}/knowledge/documents/upload`

Accepts `multipart/form-data` with:

- `file`: required
- `title`: optional; defaults to the sanitized filename without extension

Supported extensions are PDF, DOCX, TXT, MD, CSV, and JSON. Extraction runs server-side. Upload size is limited to 10 MB and the original filename is reduced to its safe base filename before persistence.

### POST `/api/workspaces/{workspaceId}/knowledge/documents/{documentId}/reindex`

Queues a new indexing attempt for a `Ready` or `Failed` document and returns `202 Accepted`. Calling reindex while the document is already `Queued` or `Processing` is idempotent and does not create duplicate work.

### DELETE `/api/workspaces/{workspaceId}/knowledge/documents/{documentId}`

Deletes a document belonging to the active workspace. Related processing jobs, chunks, and pgvector rows are removed through database cascades.

### GET `/api/workspaces/{workspaceId}/knowledge/search?query=...&limit=5`

Embeds the query and performs workspace-filtered hybrid retrieval. PostgreSQL produces a wider semantic + lexical candidate set, low-score and unsafe retrieved-content candidates are filtered, then a deterministic reranker applies query coverage / phrase boosts and document diversification before returning the final result set. The maximum final result limit is 10.

Representative knowledge errors:
- `title_required`
- `title_too_long`
- `content_required`
- `content_too_long`
- `source_name_too_long`
- `file_required`
- `file_too_large`
- `unsupported_file_type`
- `file_parse_failed`
- `document_not_found`
- `query_required`
- `query_too_long`
- `embedding_runtime_unavailable`
- `vector_store_unavailable`
- `workspace_not_found`

## Tool execution endpoints

All tool endpoints require a valid access token and membership in the addressed workspace. Tool authorization is enforced server-side and is not delegated to the AI runtime.

### GET `/api/workspaces/{workspaceId}/tools`

Returns only tools the current workspace role is allowed to request, including risk class, approval requirement, minimum roles, and typed argument schema.

### POST `/api/workspaces/{workspaceId}/tool-executions`

Request:

```json
{
  "toolName": "workspace.echo",
  "arguments": {
    "text": "hello"
  },
  "idempotencyKey": "client-generated-key-123"
}
```

The server validates workspace membership, requester role, tool schema, and idempotency before persistence or execution.

- `ReadOnly` tools enter `Ready` and execute immediately.
- `SensitiveWrite` tools persist as `PendingApproval` and do not execute until approved.

Reusing the same idempotency key with the same canonical arguments returns the original execution. Reusing it with different arguments returns `409 idempotency_conflict`.

Phase 4.5 packet C bounds (see `docs/PHASE-4.5-C-BUDGETS-SECRETS.md`):

- Request bodies above 32 KiB (4 KiB for tool policy updates) are rejected with `413 request_body_too_large` before JSON parsing.
- Credential-named fields (for example `password`, `apiKey`, `client_secret`) and recognised credential formats in arguments or the idempotency key are rejected with `400 credential_rejected`. Errors name the JSON path and detector, never the value.
- Each workspace has a request quota per tool and fixed window. A new admission beyond it returns `429 tool_quota_exceeded` with a `Retry-After` header and `retryAfterSeconds` in the body. An exact idempotent retry of an admitted execution is never charged again and is answered with that execution even while the quota is exhausted.
- Results, arguments and idempotency keys in responses are redacted; recognised credentials appear as `[REDACTED]`.

### GET `/api/workspaces/{workspaceId}/tool-executions`

Lists recent workspace-scoped tool executions.

### GET `/api/workspaces/{workspaceId}/tool-executions/{executionId}`

Returns one execution only through the active workspace membership boundary, including persisted arguments/result and audit events.

### POST `/api/workspaces/{workspaceId}/tool-executions/{executionId}/approve`

Only applies to approval-required tools.

The approver must satisfy the tool's minimum approver role and must be a different user from the requester. Successful approval transitions the execution to `Ready`, then the server executes it.

### POST `/api/workspaces/{workspaceId}/tool-executions/{executionId}/reject`

Rejects a pending sensitive execution. Rejected executions are terminal.

Representative tool errors:

- `tool_not_found`
- `execution_not_found`
- `invalid_arguments`
- `invalid_idempotency_key`
- `idempotency_conflict`
- `forbidden`
- `self_approval_forbidden`
- `approval_not_required`
- `invalid_state`
- `tool_execution_failed`
- `workspace_not_found`
- `credential_rejected` (400)
- `request_body_too_large` (413)
- `tool_quota_exceeded` (429, `Retry-After`)

## Roles

```text
Owner > Admin > Member
```

Phase 1 establishes role enforcement. Invitation/member-management workflows are intentionally deferred.

## Rate limiting

Authentication endpoints are protected by a fixed-window per-IP policy. The current Phase 1 limit is 10 auth requests per minute per partition.

## Error behavior

Representative error codes:

- `email_exists`
- `invalid_credentials`
- `missing_refresh_token`
- `invalid_refresh_token`
- `csrf_required`
- `workspace_not_found`

Authorization is enforced on the server. Frontend route guards are user-experience behavior only.
