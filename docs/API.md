# ICEHOTT API Contract — Phases 1–3

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

The API validates the request, chunks the text, requests embeddings from FastAPI in batches, stores document/chunk metadata in PostgreSQL, and stores 64-dimensional vectors in pgvector. A document is marked `Ready` only after vector indexing succeeds.

### POST `/api/workspaces/{workspaceId}/knowledge/documents/upload`

Accepts `multipart/form-data` with:

- `file`: required
- `title`: optional; defaults to the sanitized filename without extension

Supported extensions are PDF, DOCX, TXT, MD, CSV, and JSON. Extraction runs server-side. Upload size is limited to 10 MB and the original filename is reduced to its safe base filename before persistence.

### DELETE `/api/workspaces/{workspaceId}/knowledge/documents/{documentId}`

Deletes a document belonging to the active workspace. Related chunks and pgvector rows are removed through database cascades.

### GET `/api/workspaces/{workspaceId}/knowledge/search?query=...&limit=5`

Embeds the query and performs workspace-filtered hybrid retrieval: 80% cosine vector similarity plus 20% PostgreSQL lexical ranking. The maximum result limit is 10.

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
