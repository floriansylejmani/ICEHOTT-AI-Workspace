# ICEHOTT AI Workspace

![CI](https://github.com/floriansylejmani/ICEHOTT-AI-Workspace/actions/workflows/ci.yml/badge.svg)

ICEHOTT is a production-oriented AI workspace for agentic work: knowledge, tools, workflows, approvals, artifacts, and observability in one product.

## Current status

### Phase 0 — Foundation
- Next.js 16 frontend
- ASP.NET Core 9 API with Clean Architecture boundaries
- Python FastAPI AI service
- PostgreSQL + pgvector
- Redis
- Docker Compose
- GitHub Actions CI
- Health/readiness endpoints
- Architecture, security, PRD, and AI-evaluation baselines

### Phase 1 — Identity & Workspaces
- Register / login / logout / current-user flow
- ASP.NET Core password hashing
- Short-lived JWT access tokens kept in frontend memory
- HttpOnly refresh-token cookie with server-side SHA-256 hash storage
- Refresh-token rotation and replay rejection
- CSRF header requirement for refresh/logout cookie actions
- Authentication rate limiting
- Workspace creation and membership
- Owner / Admin / Member roles
- Server-side tenant isolation
- Server-side admin settings gate
- Next.js login, registration, and workspace shell
- EF Core PostgreSQL migration
- API and frontend integration tests
- CI migration validation against a real pgvector/PostgreSQL service

### Phase 2 — AI Agent Runtime
- Authenticated workspace-scoped AI chat
- Conversation and message history persisted in PostgreSQL
- ASP.NET Core → FastAPI runtime boundary
- Tenant isolation enforced before conversation access
- Active `Ask ICEHOTT…` dashboard input
- Latest conversation restoration and new-chat flow
- Phase 2 EF Core migration
- Backend, frontend, and AI runtime tests
- Docker AI health dependency and runtime verification

## Architecture

```text
Browser / Next.js
      |
      | HTTPS + access token
      v
ASP.NET Core API
      |
      +---- PostgreSQL + pgvector
      |
      +---- Redis
      |
      v
Python AI service
      |
      v
Model / tool runtime
```

The AI runtime is intentionally separate from identity and core product authorization. Tool execution and model-provider integrations are added in later phases.

## Local services

- Web: http://localhost:3000
- API: http://localhost:5050
- AI service: http://localhost:8000
- PostgreSQL: localhost:5432
- Redis: localhost:6379

## Local setup

```bash
docker compose up -d postgres redis
dotnet tool restore
dotnet ef database update --project backend/src/ICEHOTT.Persistence/ICEHOTT.Persistence.csproj --startup-project backend/src/ICEHOTT.API/ICEHOTT.API.csproj
```

Run the API:

```bash
dotnet run --project backend/src/ICEHOTT.API/ICEHOTT.API.csproj
```

Run the web app:

```bash
cd apps/web
npm ci
npm run dev
```

Run the AI service:

```bash
cd ai/icehott-ai-service
python -m uvicorn app.main:app --reload
```

Copy `.env.example` to your local environment and replace development placeholders. Never commit real credentials.

## Validation

The merge gate requires:
- .NET build with zero errors
- backend tests
- frontend lint
- frontend tests
- Next.js production build
- AI tests
- EF migration script validation
- migration application to pgvector/PostgreSQL in GitHub Actions

See [Phase 1](docs/PHASE-1-IDENTITY-WORKSPACES.md), [Phase 2](docs/PHASE-2-AGENT-RUNTIME.md), [API contract](docs/API.md), [Architecture](docs/ARCHITECTURE.md), and [Security](docs/SECURITY.md).
