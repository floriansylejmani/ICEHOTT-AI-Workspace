# ICEHOTT Architecture

## System boundaries

- `apps/web`: Next.js 16 user experience
- `backend`: ASP.NET Core API and business domain
- `ai/icehott-ai-service`: Python FastAPI AI runtime
- PostgreSQL + pgvector: application data and embeddings
- Redis: caching, coordination, and background-work primitives

## Request path

```text
Browser -> Next.js -> ASP.NET API -> AI service -> model/tool runtime
                              |          |
                              v          v
                         PostgreSQL    Redis
                         + pgvector
```

## Architectural rules

1. Domain code must not depend on infrastructure frameworks.
2. AI provider SDKs stay behind application interfaces.
3. Tool execution is permission-aware and auditable.
4. Sensitive external actions require approval by default.
5. Workspace data must be tenant-scoped at every persistence boundary.
6. Production secrets never live in source control.
