# ICEHOTT API Contract — Phase 1

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
