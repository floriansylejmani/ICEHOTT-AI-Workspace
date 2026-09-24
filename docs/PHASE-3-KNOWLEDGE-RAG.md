# Phase 3 — Knowledge / RAG

## Delivered

Phase 3 grounds ICEHOTT chat responses in workspace-owned knowledge.

- Workspace-scoped knowledge documents and deletion
- Pasted-text ingestion
- Server-side extraction for PDF, DOCX, TXT, MD, CSV, and JSON
- 10 MB upload ceiling and safe filename normalization
- Deterministic overlapping chunking
- Batched embedding requests for large accepted documents
- 64-dimensional local embedding endpoint in FastAPI
- PostgreSQL pgvector storage
- HNSW cosine-similarity index
- PostgreSQL GIN full-text index
- Hybrid semantic + lexical retrieval
- Retrieval context passed into the AI runtime
- Backend-derived source citations
- Citation persistence with conversation history
- Dashboard upload, ingestion, retrieval testing, and delete UI
- RAG citations rendered under assistant messages
- Backend, frontend, AI, parser, and integration tests

## Runtime flow

Knowledge ingestion:

```text
Browser
  |
  +--> pasted text ------------------------------+
  |                                              |
  +--> PDF/DOCX/TXT/MD/CSV/JSON upload           |
         |                                       |
         v                                       |
  server-side extraction                         |
         |                                       |
         +---------------------------------------+
                         |
                         v
                 membership gate
                         |
                         v
              overlapping chunking
                         |
                         v
              batched embeddings
                         |
                         v
        PostgreSQL metadata + pgvector
```
RAG chat:

```text
User prompt
   |
   v
query embedding
   |
   v
workspace-scoped hybrid search
(vector similarity + lexical rank)
   |
   v
retrieved chunks
   |
   v
FastAPI /v1/chat
   |
   +--> assistant response
   +--> persisted citations
   v
dashboard
```

## Data model

`knowledge_documents` stores workspace ownership, source metadata, original extracted text, indexing status, chunk count, and timestamps.

`knowledge_chunks` stores ordered text chunks scoped to both document and workspace.

`knowledge_chunk_embeddings` is a PostgreSQL-only table with `Embedding vector(64)`, a workspace index, and an HNSW `vector_cosine_ops` index.

A GIN expression index on `to_tsvector('simple', "Content")` supports lexical ranking for hybrid retrieval.

`conversation_message_citations` stores message/source/chunk IDs, source labels, and retrieval score so citations survive conversation reloads.

## Tenant isolation

The API verifies workspace membership before listing, ingesting, uploading, deleting, searching, retrieving RAG context, or reading citations.

The retrieval query independently scopes embedding rows, chunks, and documents by the same `WorkspaceId`. A document ID from another workspace is never accepted for deletion through the active workspace route.
## Ingestion safety and lifecycle

- Upload size is capped at 10 MB server-side.
- Supported extensions are explicitly allowlisted.
- Client-provided paths are removed with base-filename normalization before persistence.
- Malformed PDF/DOCX files fail parsing instead of being indexed.
- Extracted text is still subject to the 200,000-character knowledge limit.
- Documents are marked `Ready` only after vector indexing succeeds.
- Failed indexing leaves the document marked `Failed`.
- Document deletion cascades to chunks and pgvector rows.

For production internet-facing deployments, malware scanning and stronger file-signature inspection remain recommended hardening steps.

## Embedding baseline

The current FastAPI embedding implementation is deterministic and local. It hashes normalized tokens into a 64-dimensional unit vector. This is a reproducible engineering baseline, not a claim of production semantic quality.

The FastAPI embedding endpoint accepts at most 64 texts per request. ICEHOTT therefore batches document chunks in groups of 64 and validates embedding counts and dimensions across batches.

The `IAiRuntimeClient` and `IVectorStore` boundaries allow a production embedding/model provider to replace the baseline without changing authorization, API contracts, persistence ownership, or the UI.

## Retrieval

Knowledge search combines:

- 80% pgvector cosine similarity
- 20% PostgreSQL full-text lexical rank

Chat takes the best workspace-scoped matches, applies a minimum score guard, passes the selected chunks to the AI runtime, and persists citation metadata derived from the server retrieval results rather than model-generated source names.
## API surface

- `GET /api/workspaces/{workspaceId}/knowledge/documents`
- `POST /api/workspaces/{workspaceId}/knowledge/documents`
- `POST /api/workspaces/{workspaceId}/knowledge/documents/upload`
- `DELETE /api/workspaces/{workspaceId}/knowledge/documents/{documentId}`
- `GET /api/workspaces/{workspaceId}/knowledge/search`
- `POST /v1/embeddings` on the internal FastAPI runtime

## Migrations

- `20260924113819_Phase3KnowledgeRag`: knowledge documents/chunks/citations, pgvector extension, vector table, HNSW index.
- `20260924121721_Phase3KnowledgeHardening`: GIN full-text search index for hybrid retrieval.
- `20260924123531_Phase3VectorStoreIntegrity`: workspace foreign key for embedding rows; vector writes also verify chunk/workspace ownership before upsert.

## Validation

Final validation performed on September 24, 2026 includes:

- Debug and Release .NET test suites
- direct TXT/DOCX/PDF extractor tests
- large-document embedding batch test
- workspace-scoped upload/delete integration test
- tenant-isolated RAG/citation integration test
- frontend upload/delete/search tests
- multipart auth-header regression test
- frontend lint and Next.js production build
- FastAPI tests
- idempotent EF migration script generation
- migration application to Docker PostgreSQL
- pgvector extension / vector(64) / HNSW / GIN verification
- Docker API and AI image build
- live health, embeddings, retrieval SQL, and frontend HTTP smoke checks
- real PostgreSQL vector smoke transaction returned cosine similarity `1.000` and was rolled back
- embedding rows now have both chunk and workspace referential integrity

At the final pre-commit gate the suites contain 17 backend tests, 8 frontend tests, and 5 AI-service tests.

## Next phase

Phase 4 can add agent tools and execution: tool registry, permissions, structured tool calls, approval gates, audit trails, and safe external actions.
