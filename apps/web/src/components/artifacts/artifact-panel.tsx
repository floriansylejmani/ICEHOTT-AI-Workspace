"use client";

import { FormEvent, useCallback, useEffect, useState } from "react";

type ApiFetch = (path: string, init?: RequestInit) => Promise<Response>;

type Artifact = {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  sha256: string;
  status: string;
  createdByUserId: string;
  workflowRunId: string | null;
  stepRunId: string | null;
  createdAtUtc: string;
  failedAtUtc: string | null;
  deletedAtUtc: string | null;
};

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let unit = units[0];
  for (let index = 1; index < units.length && value >= 1024; index += 1) {
    value /= 1024;
    unit = units[index];
  }
  return `${value.toFixed(value >= 10 ? 0 : 1)} ${unit}`;
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value));
}

async function readApiError(response: Response) {
  try {
    const body = (await response.json()) as { code?: string };
    return body.code ?? `request_failed_${response.status}`;
  } catch {
    return `request_failed_${response.status}`;
  }
}

function createIdempotencyKey() {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) return crypto.randomUUID();
  return `artifact-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

export function ArtifactPanel({
  workspaceId,
  apiFetch,
}: {
  workspaceId: string | null;
  apiFetch: ApiFetch;
}) {
  const [artifacts, setArtifacts] = useState<Artifact[]>([]);
  const [file, setFile] = useState<File | null>(null);
  const [loading, setLoading] = useState(false);
  const [mutating, setMutating] = useState("");
  const [error, setError] = useState("");

  const loadArtifacts = useCallback(async () => {
    setError("");
    if (!workspaceId) {
      setArtifacts([]);
      return;
    }

    setLoading(true);
    try {
      const response = await apiFetch(`/api/workspaces/${workspaceId}/artifacts?limit=100`);
      if (!response.ok) throw new Error(await readApiError(response));
      setArtifacts((await response.json()) as Artifact[]);
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "artifact_load_failed");
    } finally {
      setLoading(false);
    }
  }, [workspaceId, apiFetch]);

  useEffect(() => {
    if (!workspaceId) return;
    let cancelled = false;

    async function bootstrap() {
      try {
        const response = await apiFetch(`/api/workspaces/${workspaceId}/artifacts?limit=100`);
        if (!response.ok) throw new Error(await readApiError(response));
        const nextArtifacts = (await response.json()) as Artifact[];
        if (!cancelled) setArtifacts(nextArtifacts);
      } catch (nextError) {
        if (!cancelled) {
          setError(nextError instanceof Error ? nextError.message : "artifact_load_failed");
        }
      }
    }

    void bootstrap();
    return () => { cancelled = true; };
  }, [workspaceId, apiFetch]);

  async function upload(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!workspaceId || !file || mutating) return;

    setMutating("upload");
    setError("");
    try {
      const body = new FormData();
      body.append("file", file);
      body.append("idempotencyKey", createIdempotencyKey());

      const response = await apiFetch(`/api/workspaces/${workspaceId}/artifacts/upload`, {
        method: "POST",
        body,
      });
      if (!response.ok) throw new Error(await readApiError(response));
      setFile(null);
      const input = document.getElementById("artifact-file") as HTMLInputElement | null;
      if (input) input.value = "";
      await loadArtifacts();
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "artifact_upload_failed");
    } finally {
      setMutating("");
    }
  }

  async function download(artifact: Artifact) {
    if (!workspaceId || mutating) return;
    setMutating(`download-${artifact.id}`);
    setError("");
    try {
      const response = await apiFetch(`/api/workspaces/${workspaceId}/artifacts/${artifact.id}/content`);
      if (!response.ok) throw new Error(await readApiError(response));
      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = artifact.fileName;
      anchor.click();
      URL.revokeObjectURL(url);
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "artifact_download_failed");
    } finally {
      setMutating("");
    }
  }

  async function remove(artifact: Artifact) {
    if (!workspaceId || mutating) return;
    setMutating(`delete-${artifact.id}`);
    setError("");
    try {
      const response = await apiFetch(`/api/workspaces/${workspaceId}/artifacts/${artifact.id}`, {
        method: "DELETE",
      });
      if (!response.ok) throw new Error(await readApiError(response));
      setArtifacts((current) => current.filter((item) => item.id !== artifact.id));
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "artifact_delete_failed");
    } finally {
      setMutating("");
    }
  }

  if (!workspaceId) {
    return (
      <section className="rounded-[26px] border border-white/10 bg-white/[0.035] p-8 text-sm text-white/35">
        Create or select a workspace to browse artifacts.
      </section>
    );
  }

  const totalBytes = artifacts.reduce((sum, item) => sum + item.sizeBytes, 0);

  return (
    <section className="rounded-[26px] border border-white/10 bg-white/[0.035] p-6 sm:p-8">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="text-xs uppercase tracking-[0.22em] text-amber-200/60">Durable files</p>
          <h2 className="mt-2 text-3xl font-medium">Artifact browser</h2>
          <p className="mt-2 max-w-2xl text-sm leading-6 text-white/35">
            Upload, inspect, download, and delete workspace artifacts through the authorized storage API.
          </p>
        </div>
        <div className="text-right text-xs text-white/30">
          <p>{artifacts.length} artifacts</p>
          <p className="mt-1">{formatBytes(totalBytes)} visible</p>
        </div>
      </div>

      {error && (
        <div role="alert" className="mt-5 rounded-xl border border-red-300/15 bg-red-300/[0.05] px-4 py-3 text-xs text-red-100/80">
          {error}
        </div>
      )}

      <form onSubmit={upload} className="mt-7 flex flex-col gap-3 rounded-2xl border border-white/8 bg-black/20 p-4 sm:flex-row sm:items-center">
        <input
          id="artifact-file"
          aria-label="Artifact file"
          type="file"
          onChange={(event) => setFile(event.target.files?.[0] ?? null)}
          className="min-w-0 flex-1 text-xs text-white/45 file:mr-4 file:rounded-lg file:border-0 file:bg-white file:px-3 file:py-2 file:text-xs file:font-medium file:text-black"
        />
        <button
          disabled={!file || Boolean(mutating)}
          className="h-10 rounded-lg bg-white px-5 text-xs font-medium text-black disabled:opacity-40"
        >
          {mutating === "upload" ? "Uploading…" : "Upload artifact"}
        </button>
        <button
          type="button"
          onClick={() => void loadArtifacts()}
          disabled={loading}
          className="h-10 rounded-lg border border-white/10 px-4 text-xs text-white/50 hover:text-white disabled:opacity-40"
        >
          {loading ? "Refreshing…" : "Refresh"}
        </button>
      </form>

      <div className="mt-5 overflow-hidden rounded-2xl border border-white/8">
        <div className="hidden grid-cols-[minmax(220px,1fr)_120px_130px_180px] gap-4 border-b border-white/8 bg-black/20 px-4 py-3 text-[10px] uppercase tracking-[0.18em] text-white/25 md:grid">
          <span>Artifact</span><span>Size</span><span>Status</span><span>Actions</span>
        </div>

        <div className="divide-y divide-white/8">
          {artifacts.map((artifact) => (
            <div
              key={artifact.id}
              className="grid gap-3 px-4 py-4 md:grid-cols-[minmax(220px,1fr)_120px_130px_180px] md:items-center md:gap-4"
            >
              <div className="min-w-0">
                <p className="truncate text-sm font-medium">{artifact.fileName}</p>
                <p className="mt-1 truncate text-[10px] text-white/25">
                  {artifact.contentType} · SHA-256 {artifact.sha256.slice(0, 12)}…
                </p>
                <p className="mt-1 text-[10px] text-white/20">
                  {formatDate(artifact.createdAtUtc)}
                  {artifact.workflowRunId ? " · workflow artifact" : " · workspace artifact"}
                </p>
              </div>
              <p className="text-xs text-white/45">{formatBytes(artifact.sizeBytes)}</p>
              <span className="w-fit rounded-full border border-emerald-300/15 bg-emerald-300/[0.06] px-2.5 py-1 text-[10px] text-emerald-200/75">
                {artifact.status}
              </span>
              <div className="flex gap-2">
                <button
                  type="button"
                  onClick={() => void download(artifact)}
                  disabled={Boolean(mutating) || artifact.status !== "Ready"}
                  className="rounded-lg border border-white/10 px-3 py-2 text-xs text-white/55 hover:text-white disabled:opacity-35"
                >
                  Download
                </button>
                <button
                  type="button"
                  onClick={() => void remove(artifact)}
                  disabled={Boolean(mutating)}
                  className="rounded-lg border border-red-300/15 px-3 py-2 text-xs text-red-100/65 disabled:opacity-35"
                >
                  Delete
                </button>
              </div>
            </div>
          ))}
          {!loading && artifacts.length === 0 && (
            <div className="px-5 py-12 text-center">
              <p className="text-sm text-white/35">No artifacts yet.</p>
              <p className="mt-1 text-xs text-white/20">Upload a file or let a workflow create one.</p>
            </div>
          )}
        </div>
      </div>
    </section>
  );
}
