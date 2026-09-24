# Phase 1: Identity and Workspaces

## Status

Implementation complete on `feature/phase-1-identity-workspaces`. Merge requires a green GitHub Actions gate.

## Goal

Build the first production-oriented vertical slice for ICEHOTT: secure identity, workspace membership, and tenant authorization boundaries.

## Delivered

- User registration and login
- Password hashing with ASP.NET Core PasswordHasher
- Short-lived JWT access tokens
- Refresh-session persistence with opaque random refresh tokens
- Refresh token stored only as an HttpOnly cookie
- SHA-256 refresh-token hashes stored in PostgreSQL
- Refresh-token rotation and replay rejection
- Logout with server-side refresh-session revocation
- CSRF custom-header gate for refresh/logout
- Per-IP authentication rate limiting
- Current-user endpoint
- Workspace creation/list/get endpoints
- Owner, Admin, and Member role model
- Workspace creator automatically becomes Owner
- Non-member workspace lookups return 404
- Member access to admin workspace settings returns 403
- PostgreSQL EF Core persistence and migration
- Next.js landing, login, register, and protected workspace shell
- Access token held in browser memory, not localStorage
- Automatic refresh + one-time retry after protected API 401
- Frontend, backend, and AI CI jobs
- Real pgvector/PostgreSQL migration application in CI

## Acceptance criteria

- [x] User can register and log in.
- [x] Passwords are never stored in plaintext.
- [x] Authenticated user can create a workspace.
- [x] Workspace creator becomes Owner.
- [x] Unauthorized users cannot read another workspace.
- [x] Role checks are enforced server-side.
- [x] Refresh tokens rotate and replay is rejected.
- [x] Cookie-authenticated refresh/logout require a CSRF header.
- [x] Authentication endpoints are rate limited.
- [x] Health and readiness endpoints remain available.
- [x] Backend integration tests pass locally.
- [x] Frontend tests, lint, and production build pass locally.
- [x] API and architecture docs are updated.
- [ ] Final branch CI is green before merge.

## Non-goals

- Social login
- Email invitations
- Billing
- SSO/SAML
- AI agents
- RAG / embeddings
- Autonomous tool execution

## Security boundary

Every workspace-owned resource is server-scoped by authenticated user membership. Frontend route guards never substitute for API authorization.

The refresh cookie is not exposed to JavaScript. The frontend stores only the short-lived access token in memory and can recover the session through the refresh endpoint.

## Validation evidence

Local validation:
- .NET build: **0 warnings / 0 errors**
- Backend integration tests: **9 passed**
- Frontend tests: **3 passed**
- Frontend lint: **passed**
- Next.js production build: **passed**
- AI tests: **passed**
- Docker Compose syntax: **valid**
- NuGet vulnerability scan: **0 known vulnerabilities**
- npm audit: **0 vulnerabilities**
- pip-audit: **0 known vulnerabilities**

CI validation:
- Generates an idempotent EF migration script.
- Starts a real `pgvector/pgvector:pg16` service.
- Applies the Phase 1 migration to PostgreSQL.
- Runs backend tests after migration application.
- Runs frontend install/lint/tests/build.
- Runs FastAPI tests.

## Environment note

Local Docker Desktop requires elevated Windows service access on the current machine. The application does not treat that local environment limitation as database validation: PostgreSQL migration execution is enforced in GitHub Actions on every pull request.

## Next phase

Phase 2 begins only after this branch is merged green. Phase 2 scope: AI chat runtime, streaming, conversation persistence, and model-provider abstraction. Tool execution and RAG remain later phases.
