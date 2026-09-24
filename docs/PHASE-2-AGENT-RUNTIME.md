# Phase 2 — AI Agent Runtime

## Delivered

Phase 2 activates the first workspace-scoped ICEHOTT AI conversation flow.

- Authenticated chat endpoint in ASP.NET Core
- Server-side workspace membership enforcement
- Conversation and message persistence in PostgreSQL
- Backend HTTP boundary to the Python FastAPI AI service
- Local baseline AI runtime at `POST /v1/chat`
- Dashboard `Ask ICEHOTT…` input activated
- Latest conversation history restored per workspace
- New-chat action and workspace switching
- Backend, frontend, and AI tests
- EF Core migration `Phase2AgentRuntime`
- Docker health dependency between API and AI service

## Runtime request path

```text
Next.js AgentPanel
      |
      | JWT + workspaceId
      v
ASP.NET Core AgentController
      |
      +--> membership check
      |
      +--> PostgreSQL conversation/message history
      |
      v
IAiRuntimeClient
      |
      v
FastAPI /v1/chat
      |
      v
baseline runtime response
      |
      +--> assistant message persisted
      v
Next.js dashboard
```

## Data model

`conversations`
- `Id`
- `WorkspaceId`
- `CreatedByUserId`
- `Title`
- `CreatedAtUtc`
- `UpdatedAtUtc`

`conversation_messages`
- `Id`
- `ConversationId`
- `WorkspaceId`
- `Role`
- `Content`
- `CreatedAtUtc`

Every API read/write is gated by workspace membership before conversation data is accessed.

## AI boundary

The current FastAPI runtime is intentionally deterministic and local. It proves the complete service boundary and persistence path without coupling ICEHOTT to a model vendor.

`IAiRuntimeClient` is the provider boundary. A production model integration can be added behind that interface without changing the domain model, authorization rules, controller contract, or frontend chat component.

## Validation

Validated locally on September 24, 2026:

- Backend: 10 tests passed
- Frontend: 4 tests passed
- Frontend lint: passed
- Next.js production build: passed
- AI service: 2 tests passed
- Docker API image: built
- Docker AI image: built and healthy
- PostgreSQL: healthy
- Redis: healthy
- Migration `20260924110340_Phase2AgentRuntime`: applied
- API `/health`: healthy
- AI `/health`: healthy
- AI `/v1/chat`: returned the Phase 2 response
- Frontend `/app`: HTTP 200

## Next phase

Phase 3 adds Knowledge/RAG: document ingestion, chunking, embeddings, pgvector retrieval, source citations, and workspace-scoped knowledge permissions.
