"use client";

import { FormEvent, useCallback, useEffect, useState } from "react";

type ApiFetch = (path: string, init?: RequestInit) => Promise<Response>;

type KnowledgeDocument = {
  id: string;
  title: string;
  sourceName: string | null;
  status: string;
  chunkCount: number;
  characterCount: number;
  createdAtUtc: string;
  indexedAtUtc: string | null;
};

type KnowledgeSearchResult = {
  chunkId: string;
  documentId: string;
  title: string;
  sourceName: string | null;
  content: string;
  score: number;
};

export function KnowledgePanel({
  workspaceId,
  apiFetch,
}: {
  workspaceId: string | null;
  apiFetch: ApiFetch;
}) {
  const [documents, setDocuments] = useState<KnowledgeDocument[]>([]);
  const [title, setTitle] = useState("");
  const [sourceName, setSourceName] = useState("");
  const [content, setContent] = useState("");
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<KnowledgeSearchResult[]>([]);
  const [ingesting, setIngesting] = useState(false);
  const [searching, setSearching] = useState(false);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [error, setError] = useState("");

  const loadDocuments = useCallback(async () => {
    if (!workspaceId) return [];
    const response = await apiFetch(
      "/api/workspaces/" + workspaceId + "/knowledge/documents",
    );
    if (!response.ok) throw new Error("knowledge_load_failed");
    return (await response.json()) as KnowledgeDocument[];
  }, [workspaceId, apiFetch]);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      setDocuments([]);
      setResults([]);
      setError("");
      if (!workspaceId) return;

      try {
        const items = await loadDocuments();
        if (!cancelled) setDocuments(items);
      } catch {
        if (!cancelled) setError("Could not load workspace knowledge.");
      }
    }

    void load();
    return () => {
      cancelled = true;
    };
  }, [workspaceId, loadDocuments]);

  async function ingest(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!workspaceId || ingesting) return;
    if (!selectedFile && (!title.trim() || !content.trim())) return;

    setIngesting(true);
    setError("");
    try {
      let response: Response;

      if (selectedFile) {
        const form = new FormData();
        form.append("file", selectedFile);
        if (title.trim()) form.append("title", title.trim());

        response = await apiFetch(
          "/api/workspaces/" + workspaceId + "/knowledge/documents/upload",
          {
            method: "POST",
            body: form,
          },
        );
      } else {
        response = await apiFetch(
          "/api/workspaces/" + workspaceId + "/knowledge/documents",
          {
            method: "POST",
            body: JSON.stringify({
              title: title.trim(),
              sourceName: sourceName.trim() || null,
              content: content.trim(),
            }),
          },
        );
      }

      if (!response.ok) throw new Error("knowledge_ingest_failed");
      setDocuments(await loadDocuments());
      setTitle("");
      setSourceName("");
      setContent("");
      setSelectedFile(null);
    } catch {
      setError("Knowledge indexing failed. Check the file, AI runtime, and vector store.");
    } finally {
      setIngesting(false);
    }
  }

  async function search(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!workspaceId || !query.trim() || searching) return;

    setSearching(true);
    setError("");
    try {
      const response = await apiFetch(
        "/api/workspaces/" +
          workspaceId +
          "/knowledge/search?query=" +
          encodeURIComponent(query.trim()) +
          "&limit=5",
      );

      if (!response.ok) throw new Error("knowledge_search_failed");
      setResults((await response.json()) as KnowledgeSearchResult[]);
    } catch {
      setError("Knowledge search failed.");
    } finally {
      setSearching(false);
    }
  }

  async function deleteDocument(documentId: string) {
    if (!workspaceId || deletingId) return;

    setDeletingId(documentId);
    setError("");
    try {
      const response = await apiFetch(
        "/api/workspaces/" + workspaceId + "/knowledge/documents/" + documentId,
        { method: "DELETE" },
      );
      if (!response.ok) throw new Error("knowledge_delete_failed");

      setDocuments((current) => current.filter((item) => item.id !== documentId));
      setResults((current) => current.filter((item) => item.documentId !== documentId));
    } catch {
      setError("Could not delete the knowledge document.");
    } finally {
      setDeletingId(null);
    }
  }

  return (
    <section className="mt-8 rounded-[26px] border border-white/10 bg-white/[0.025] p-6 sm:p-8">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="text-xs uppercase tracking-[0.22em] text-amber-200/55">
            Knowledge / RAG
          </p>
          <h2 className="mt-2 text-xl font-medium">Ground ICEHOTT in workspace knowledge.</h2>
          <p className="mt-2 max-w-2xl text-sm text-white/35">
            Upload PDF, DOCX, TXT, MD, CSV, or JSON. ICEHOTT extracts, chunks, embeds,
            and retrieves knowledge inside the active workspace.
          </p>
        </div>
        <span className="rounded-full border border-emerald-300/15 bg-emerald-300/5 px-3 py-1.5 text-xs text-emerald-200/70">
          pgvector active
        </span>
      </div>

      <div className="mt-7 grid gap-5 lg:grid-cols-2">
        <form onSubmit={ingest} className="space-y-3 rounded-2xl border border-white/8 bg-black/20 p-5">
          <p className="text-sm font-medium">Add knowledge</p>
          <input
            aria-label="Knowledge title"
            required
            maxLength={200}
            value={title}
            onChange={(event) => setTitle(event.target.value)}
            placeholder="Document title"
            className="h-10 w-full rounded-xl border border-white/10 bg-white/[0.03] px-3 text-sm outline-none focus:border-amber-200/35"
          />
          <input
            aria-label="Source name"
            maxLength={260}
            value={sourceName}
            disabled={Boolean(selectedFile)}
            onChange={(event) => setSourceName(event.target.value)}
            placeholder={selectedFile ? "Source comes from uploaded file" : "Source name (optional)"}
            className="h-10 w-full rounded-xl border border-white/10 bg-white/[0.03] px-3 text-sm outline-none focus:border-amber-200/35 disabled:opacity-45"
          />
          <input
            aria-label="Knowledge file"
            type="file"
            accept=".pdf,.docx,.txt,.md,.csv,.json,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document,text/plain,text/markdown,text/csv,application/json"
            onChange={(event) => {
              const file = event.target.files?.[0] ?? null;
              if (!file) {
                setSelectedFile(null);
                return;
              }
              if (file.size > 10 * 1024 * 1024) {
                setError("Knowledge files must be 10 MB or smaller.");
                setSelectedFile(null);
                event.target.value = "";
                return;
              }

              setSelectedFile(file);
              setSourceName("");
              setContent("");
              setTitle((current) => current || file.name.replace(/\.[^.]+$/, ""));
              setError("");
            }}
            className="block w-full rounded-xl border border-dashed border-white/10 bg-white/[0.02] px-3 py-2 text-xs text-white/45 file:mr-3 file:rounded-lg file:border-0 file:bg-white file:px-3 file:py-1.5 file:text-xs file:font-medium file:text-black"
          />
          {selectedFile && (
            <div className="flex items-center justify-between rounded-xl border border-emerald-300/10 bg-emerald-300/[0.04] px-3 py-2 text-xs">
              <span className="truncate text-emerald-100/60">
                {selectedFile.name} · {(selectedFile.size / 1024).toFixed(0)} KB
              </span>
              <button
                type="button"
                onClick={() => setSelectedFile(null)}
                className="ml-3 text-white/35 hover:text-white"
              >
                Clear
              </button>
            </div>
          )}
          <textarea
            aria-label="Knowledge content"
            required={!selectedFile}
            disabled={Boolean(selectedFile)}
            maxLength={200000}
            rows={7}
            value={content}
            onChange={(event) => setContent(event.target.value)}
            placeholder={
              selectedFile
                ? "The server will extract text from the selected file."
                : "Or paste workspace knowledge here…"
            }
            className="w-full resize-y rounded-xl border border-white/10 bg-white/[0.03] px-3 py-3 text-sm leading-6 outline-none focus:border-amber-200/35 disabled:opacity-45"
          />
          <button
            type="submit"
            disabled={
              !workspaceId ||
              ingesting ||
              !title.trim() ||
              (!selectedFile && !content.trim())
            }
            className="h-10 w-full rounded-xl bg-white text-sm font-medium text-black hover:bg-amber-100 disabled:opacity-35"
          >
            {ingesting ? "Indexing…" : "Index knowledge"}
          </button>
        </form>

        <div className="space-y-5">
          <form onSubmit={search} className="rounded-2xl border border-white/8 bg-black/20 p-5">
            <p className="text-sm font-medium">Test retrieval</p>
            <div className="mt-3 flex gap-2">
              <input
                aria-label="Search knowledge"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder="Search workspace knowledge…"
                className="h-10 min-w-0 flex-1 rounded-xl border border-white/10 bg-white/[0.03] px-3 text-sm outline-none focus:border-amber-200/35"
              />
              <button
                type="submit"
                disabled={!workspaceId || searching || !query.trim()}
                className="rounded-xl border border-white/12 px-4 text-sm text-white/70 hover:text-white disabled:opacity-35"
              >
                {searching ? "…" : "Search"}
              </button>
            </div>

            <div className="mt-4 space-y-2">
              {results.map((result) => (
                <div key={result.chunkId} className="rounded-xl border border-white/8 bg-white/[0.025] p-3">
                  <div className="flex items-center justify-between gap-3 text-xs">
                    <span className="truncate text-white/65">{result.title}</span>
                    <span className="text-emerald-200/55">
                      {(result.score * 100).toFixed(0)}%
                    </span>
                  </div>
                  <p className="mt-2 line-clamp-3 text-xs leading-5 text-white/30">
                    {result.content}
                  </p>
                </div>
              ))}
              {results.length === 0 && (
                <p className="text-xs text-white/25">Retrieved chunks will appear here.</p>
              )}
            </div>
          </form>

          <div className="rounded-2xl border border-white/8 bg-black/20 p-5">
            <p className="text-sm font-medium">Indexed documents</p>
            <div className="mt-3 max-h-48 space-y-2 overflow-y-auto">
              {documents.map((document) => (
                <div key={document.id} className="rounded-xl border border-white/8 px-3 py-2.5">
                  <div className="flex items-center justify-between gap-3">
                    <p className="truncate text-xs text-white/70">{document.title}</p>
                    <div className="flex items-center gap-2">
                      <span className="text-[10px] uppercase tracking-wider text-emerald-200/55">
                        {document.status}
                      </span>
                      <button
                        type="button"
                        disabled={deletingId === document.id}
                        onClick={() => void deleteDocument(document.id)}
                        className="text-[10px] text-red-200/40 hover:text-red-200/80 disabled:opacity-30"
                      >
                        {deletingId === document.id ? "Deleting…" : "Delete"}
                      </button>
                    </div>
                  </div>
                  <p className="mt-1 text-[10px] text-white/25">
                    {document.chunkCount} chunks · {document.characterCount} chars
                    {document.sourceName ? " · " + document.sourceName : ""}
                  </p>
                </div>
              ))}
              {documents.length === 0 && (
                <p className="text-xs text-white/25">No workspace knowledge indexed yet.</p>
              )}
            </div>
          </div>
        </div>
      </div>

      {error && <p className="mt-4 text-xs text-red-200/75">{error}</p>}
    </section>
  );
}
