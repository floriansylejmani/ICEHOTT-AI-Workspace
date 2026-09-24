# ICEHOTT Architecture

## System boundaries

- `apps/web`: Next.js 16 user experience
- `backend/src/ICEHOTT.API`: HTTP/authentication boundary
- `ICEHOTT.Application`: use cases and interfaces
- `ICEHOTT.Domain`: domain entities and workspace roles
- `ICEHOTT.Infrastructure`: password/token/security implementations and the FastAPI AI HTTP client
- `ICEHOTT.Persistence`: EF Core PostgreSQL persistence and migrations
- `ai/icehott-ai-service`: Python FastAPI AI runtime
- PostgreSQL + pgvector: durable application data, vector embeddings, and RAG retrieval
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

## Phase 2 agent flow

```text
Authenticated browser
      |
      | workspaceId + prompt
      v
AgentController
      |
      +--> WorkspaceMembership gate
      |
      +--> ConversationRepository --> PostgreSQL
      |
      v
IAiRuntimeClient
      |
      v
FastAPI /v1/chat
      |
      v
assistant response
      |
      +--> persisted message
      v
dashboard
```

The FastAPI runtime is behind an application interface, so a model provider can be replaced without moving authorization or tenant-scoping logic out of ASP.NET Core.

## Phase 3 RAG flow

```text
Workspace knowledge / uploaded file
      |
      v
KnowledgeController
      |
      +--> membership gate
      +--> filename/type/size validation
      +--> server-side PDF/DOCX/text extraction
      +--> overlapping chunking
      +--> batched FastAPI /v1/embeddings
      +--> PostgreSQL metadata
      +--> pgvector vector(64) + HNSW
      +--> PostgreSQL full-text GIN index

User prompt
      |
      v
query embedding --> workspace-filtered hybrid retrieval
                  (semantic + lexical)
      |
      v
retrieved chunks --> FastAPI /v1/chat
      |
      +--> assistant response
      +--> persisted citation metadata
      v
dashboard
```

Vector persistence is isolated behind `IVectorStore`; provider/model calls remain behind `IAiRuntimeClient`. The Domain project has no pgvector or provider SDK dependency.

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
