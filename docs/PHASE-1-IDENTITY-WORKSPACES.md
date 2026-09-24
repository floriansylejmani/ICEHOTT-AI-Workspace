# Phase 1: Identity and Workspaces

## Goal

Build the first production-oriented vertical slice for ICEHOTT: secure identity, workspace membership, and tenant authorization boundaries.

## Scope

- User registration and login
- Password hashing and validation
- JWT access token and refresh/session strategy
- Workspace creation
- Owner, Admin, and Member roles
- Workspace membership
- Current-user and current-workspace endpoints
- Tenant-scoped authorization rules
- Audit-friendly security events
- Backend integration tests
- Minimal frontend authentication/workspace shell

## Acceptance criteria

- User can register and log in.
- Passwords are never stored in plaintext.
- Authenticated user can create a workspace.
- Workspace creator becomes Owner.
- Unauthorized users cannot access another workspace.
- Role checks are enforced server-side.
- Health and readiness endpoints remain green.
- Backend tests pass.
- Frontend lint and production build pass.
- CI must be green before merge.
- API and architecture docs are updated.

## Non-goals

- Social login
- Email invitations
- Billing
- SSO/SAML
- AI agents or RAG

## Security constraints

Tenant isolation is a blocking requirement. Every workspace-owned resource must be scoped on the server side. Frontend route guards are UX controls only and never replace API authorization.

## Delivery plan

1. Domain model and persistence model
2. Authentication contracts and token service
3. Registration/login endpoints
4. Workspace creation and membership
5. Authorization policies
6. Integration tests for tenant isolation
7. Minimal frontend auth shell
8. CI and documentation gate

## Phase 1A implementation status

Completed:
- User, workspace, membership, and refresh-session domain model
- PostgreSQL EF Core persistence model and initial migration
- Password hashing with ASP.NET Core PasswordHasher
- Short-lived JWT access tokens
- HttpOnly refresh-token cookie with server-side hash storage
- Refresh-token rotation and replay rejection
- Register, login, refresh, and current-user endpoints
- Workspace create/list/get endpoints
- Server-side tenant isolation
- SQLite-backed API integration test harness
- CI validation for PostgreSQL migration SQL generation

Validation evidence:
- .NET build: 0 warnings, 0 errors
- Backend tests: 6 passed, 0 failed
- Frontend lint/build: passed
- AI tests: passed
- Docker Compose configuration: valid
- PostgreSQL migration SQL generation: passed

Environment note:
- Docker runtime smoke test is pending because Docker Desktop's Linux engine was not available during this run. This is an environment gate, not a code/test failure.
