# ICEHOTT Architecture

## System boundaries

- `apps/web`: Next.js 16 user experience
- `backend/src/ICEHOTT.API`: HTTP/authentication boundary
- `ICEHOTT.Application`: use cases and interfaces
- `ICEHOTT.Domain`: domain entities and workspace roles
- `ICEHOTT.Infrastructure`: password/token/security implementations
- `ICEHOTT.Persistence`: EF Core PostgreSQL persistence and migrations
- `ai/icehott-ai-service`: Python FastAPI AI runtime
- PostgreSQL + pgvector: durable application data and future embeddings
- Redis: future caching, coordination, and background-work primitives

## Request path

```text
Browser / Next.js
      |
      | access JWT
      v
ASP.NET Core API
      |
      +------ Application ------ Domain
      |
      +------ Infrastructure
      |
      +------ Persistence ------ PostgreSQL + pgvector
      |
      +------ AI boundary ------ FastAPI (Phase 2+)
                                 |
                                 v
                          model/tool runtime
```

## Phase 1 session flow

```text
Register / Login
      |
      +--> access JWT --------> frontend memory
      |
      +--> refresh token -----> HttpOnly cookie
                 |
                 v
          SHA-256 hash only
                 |
                 v
            PostgreSQL

Protected request -> 401 -> refresh (CSRF header) -> rotate refresh -> retry once
```

## Tenant model

```text
User
  |
  +--- WorkspaceMembership --- Workspace
              |
              +--- Owner
              +--- Admin
              +--- Member
```

Every workspace access query includes the authenticated user membership boundary. Phase 1 deliberately returns 404 to non-members so another tenant's workspace existence is not disclosed.

## Architectural rules

1. Domain code must not depend on infrastructure frameworks.
2. AI provider SDKs stay behind application interfaces.
3. Workspace-owned data is server-scoped by membership.
4. Tool execution is permission-aware and auditable.
5. Sensitive external actions require approval by default.
6. Production secrets never live in source control.
7. Access tokens are short lived; refresh credentials are opaque, rotated, and server revocable.
8. CI must prove EF migrations apply to real PostgreSQL before merge.
