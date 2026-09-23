# ADR 0001: Foundation stack

## Status
Accepted

## Decision
ICEHOTT uses Next.js for the web client, ASP.NET Core for the product API and domain boundary, Python/FastAPI for the AI runtime, PostgreSQL with pgvector for durable data and embeddings, and Redis for cache/coordination primitives.

## Rationale
This split keeps product/business rules strongly typed in .NET while allowing the AI runtime to use the Python ecosystem. The web frontend remains independently deployable. PostgreSQL provides a single durable relational source of truth while pgvector supports semantic retrieval.

## Consequences
- Cross-service contracts must be explicit and versioned.
- AI provider integrations stay behind the AI service boundary.
- Production deployments require service-level observability and health checks.
- Shared concepts should be documented rather than implemented as duplicated business logic.
