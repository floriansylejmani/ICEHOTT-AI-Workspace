"use client";

import { FormEvent, useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { AgentPanel } from "@/components/agent/agent-panel";
import { ArtifactPanel } from "@/components/artifacts/artifact-panel";
import { KnowledgePanel } from "@/components/knowledge/knowledge-panel";
import { WorkflowPanel } from "@/components/workflows/workflow-panel";
import { useAuth } from "@/lib/auth";

type Workspace = {
  id: string;
  name: string;
  slug: string;
  role: number | string;
  createdAtUtc: string;
};

type Section = "Home" | "Workflows" | "Artifacts";

const navigation: Array<{
  id: Section;
  label: string;
  meta: string;
}> = [
  { id: "Home", label: "Home", meta: "AI + RAG" },
  { id: "Workflows", label: "Workflows", meta: "Durable" },
  { id: "Artifacts", label: "Artifacts", meta: "Files" },
];

const sectionCopy: Record<Section, { eyebrow: string; title: string }> = {
  Home: { eyebrow: "Workspace home", title: "Operate your AI workspace." },
  Workflows: { eyebrow: "Workflow control plane", title: "Automations with durable state." },
  Artifacts: { eyebrow: "Artifact storage", title: "Files with verified integrity." },
};

export function WorkspaceShell() {
  const router = useRouter();
  const { user, status, apiFetch, logout } = useAuth();
  const [workspaces, setWorkspaces] = useState<Workspace[]>([]);
  const [activeWorkspaceId, setActiveWorkspaceId] = useState<string | null>(null);
  const [activeSection, setActiveSection] = useState<Section>("Home");
  const [workspaceName, setWorkspaceName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const fetchWorkspaces = useCallback(async () => {
    const response = await apiFetch("/api/workspaces");
    if (!response.ok) throw new Error("workspace_load_failed");
    return (await response.json()) as Workspace[];
  }, [apiFetch]);

  useEffect(() => {
    if (status === "anonymous") {
      router.replace("/login");
      return;
    }

    if (status !== "authenticated") return;

    let cancelled = false;
    async function bootstrap() {
      try {
        const items = await fetchWorkspaces();
        if (!cancelled) {
          setWorkspaces(items);
          setActiveWorkspaceId((current) =>
            current && items.some((workspace) => workspace.id === current)
              ? current
              : (items[0]?.id ?? null),
          );
        }
      } catch {
        if (!cancelled) setError("Could not load workspaces.");
      }
    }

    void bootstrap();
    return () => {
      cancelled = true;
    };
  }, [status, router, fetchWorkspaces]);

  async function createWorkspace(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!workspaceName.trim()) return;
    setBusy(true);
    setError("");
    try {
      const response = await apiFetch("/api/workspaces", {
        method: "POST",
        body: JSON.stringify({ name: workspaceName.trim() }),
      });
      if (!response.ok) throw new Error("workspace_create_failed");

      const created = (await response.json()) as Workspace;
      setWorkspaceName("");
      setWorkspaces(await fetchWorkspaces());
      setActiveWorkspaceId(created.id);
    } catch {
      setError("Could not create workspace.");
    } finally {
      setBusy(false);
    }
  }

  if (status === "loading") {
    return (
      <div className="grid min-h-screen place-items-center bg-[#08090b] text-sm text-white/45">
        Securing workspace…
      </div>
    );
  }

  if (status !== "authenticated" || !user) return null;

  const activeWorkspace =
    workspaces.find((workspace) => workspace.id === activeWorkspaceId)
      ?? workspaces[0]
      ?? null;
  const copy = sectionCopy[activeSection];

  return (
    <main className="min-h-screen bg-[#08090b] text-white">
      <div className="grid min-h-screen lg:grid-cols-[250px_1fr] xl:grid-cols-[250px_1fr_320px]">
        <aside className="border-r border-white/10 bg-[#0a0b0d] p-5">
          <div className="mb-8 flex items-center justify-between">
            <span className="text-sm font-semibold tracking-[0.26em]">ICEHOTT</span>
            <span className="h-2 w-2 rounded-full bg-emerald-300 shadow-[0_0_14px_rgba(110,231,183,.65)]" />
          </div>

          <div className="mb-7">
            <p className="mb-2 px-2 text-[10px] uppercase tracking-[0.24em] text-white/25">
              Workspace
            </p>
            <div className="rounded-xl border border-white/10 bg-white/[0.035] p-3">
              <p className="truncate text-sm font-medium">
                {activeWorkspace?.name ?? "Create your first workspace"}
              </p>
              <p className="mt-1 text-xs text-white/30">
                {workspaces.length} workspace{workspaces.length === 1 ? "" : "s"}
              </p>
            </div>
          </div>

          <nav className="space-y-1">
            {navigation.map((item) => {
              const selected = activeSection === item.id;
              return (
                <button
                  type="button"
                  key={item.id}
                  aria-pressed={selected}
                  onClick={() => setActiveSection(item.id)}
                  className={
                    "flex w-full items-center justify-between rounded-lg px-3 py-2.5 text-left text-sm transition " +
                    (selected
                      ? "bg-white/[0.07] text-white"
                      : "text-white/38 hover:bg-white/[0.035] hover:text-white/70")
                  }
                >
                  <span>{item.label}</span>
                  <span className={selected
                    ? "text-[9px] uppercase tracking-wider text-amber-200/60"
                    : "text-[9px] uppercase tracking-wider text-white/20"}
                  >
                    {item.meta}
                  </span>
                </button>
              );
            })}
          </nav>

          <div className="mt-10 border-t border-white/10 pt-5">
            <p className="truncate text-sm">{user.displayName}</p>
            <p className="truncate text-xs text-white/30">{user.email}</p>
            <button
              onClick={async () => {
                await logout();
                router.replace("/login");
              }}
              className="mt-4 text-xs text-white/35 hover:text-white"
            >
              Sign out
            </button>
          </div>
        </aside>

        <section className="min-w-0 px-6 py-7 lg:px-10">
          <div className={activeSection === "Workflows" ? "mx-auto max-w-6xl" : "mx-auto max-w-4xl"}>
            <header className="mb-10 flex flex-wrap items-center justify-between gap-4">
              <div>
                <p className="text-xs uppercase tracking-[0.24em] text-amber-200/60">
                  {copy.eyebrow}
                </p>
                <h1 className="mt-2 text-2xl font-medium">
                  {activeSection === "Home"
                    ? `Good to see you, ${user.displayName.split(" ")[0]}.`
                    : copy.title}
                </h1>
              </div>
              <span className="rounded-full border border-emerald-300/15 bg-emerald-300/5 px-3 py-1.5 text-xs text-emerald-200/70">
                Identity secured
              </span>
            </header>

            {activeSection === "Home" && (
              <>
                <AgentPanel workspaceId={activeWorkspace?.id ?? null} apiFetch={apiFetch} />
                <KnowledgePanel workspaceId={activeWorkspace?.id ?? null} apiFetch={apiFetch} />

                <div className="mt-8 grid gap-4 md:grid-cols-2">
                  <section className="rounded-2xl border border-white/10 bg-white/[0.025] p-6">
                    <p className="text-xs uppercase tracking-[0.2em] text-white/30">Your workspaces</p>
                    <div className="mt-5 space-y-2">
                      {workspaces.map((workspace) => {
                        const selected = workspace.id === activeWorkspace?.id;
                        return (
                          <button
                            type="button"
                            key={workspace.id}
                            aria-pressed={selected}
                            onClick={() => setActiveWorkspaceId(workspace.id)}
                            className={
                              "w-full rounded-xl border px-4 py-3 text-left transition " +
                              (selected
                                ? "border-amber-200/25 bg-amber-100/[0.06]"
                                : "border-white/8 bg-black/20 hover:border-white/15")
                            }
                          >
                            <div className="flex items-center justify-between gap-4">
                              <p className="truncate text-sm font-medium">{workspace.name}</p>
                              <span className="text-[10px] uppercase tracking-wider text-amber-200/50">
                                {String(workspace.role)}
                              </span>
                            </div>
                            <p className="mt-1 truncate text-xs text-white/25">{workspace.slug}</p>
                          </button>
                        );
                      })}
                      {workspaces.length === 0 && (
                        <p className="text-sm text-white/30">No workspaces yet.</p>
                      )}
                    </div>
                  </section>

                  <section className="rounded-2xl border border-white/10 bg-white/[0.025] p-6">
                    <p className="text-xs uppercase tracking-[0.2em] text-white/30">New workspace</p>
                    <form onSubmit={createWorkspace} className="mt-5 space-y-3">
                      <input
                        required
                        minLength={2}
                        maxLength={120}
                        value={workspaceName}
                        onChange={(event) => setWorkspaceName(event.target.value)}
                        placeholder="e.g. Product & AI"
                        className="h-11 w-full rounded-xl border border-white/10 bg-black/20 px-4 text-sm outline-none focus:border-amber-200/40"
                      />
                      <button
                        disabled={busy}
                        className="h-11 w-full rounded-xl bg-white text-sm font-medium text-black hover:bg-amber-100 disabled:opacity-50"
                      >
                        {busy ? "Creating…" : "Create workspace"}
                      </button>
                      {error && <p className="text-xs text-red-200/80">{error}</p>}
                    </form>
                  </section>
                </div>
              </>
            )}

            {activeSection === "Workflows" && (
              <WorkflowPanel
                workspaceId={activeWorkspace?.id ?? null}
                workspaceRole={activeWorkspace?.role ?? null}
                apiFetch={apiFetch}
              />
            )}

            {activeSection === "Artifacts" && (
              <ArtifactPanel workspaceId={activeWorkspace?.id ?? null} apiFetch={apiFetch} />
            )}
          </div>
        </section>

        <aside className="hidden border-l border-white/10 bg-[#0a0b0d] p-6 xl:block">
          <p className="text-xs uppercase tracking-[0.22em] text-white/30">System status</p>
          <div className="mt-5 space-y-3">
            {[
              ["Authentication", "Active"],
              ["Tenant isolation", "Active"],
              ["Agent runtime", "Active"],
              ["Knowledge / RAG", "Active"],
              ["Durable workflows", "Active"],
              ["Artifact storage", "Active"],
              ["Scheduler", "Active"],
            ].map(([label, value]) => (
              <div
                key={label}
                className="flex items-center justify-between rounded-xl border border-white/8 p-3 text-xs"
              >
                <span className="text-white/45">{label}</span>
                <span className="text-emerald-200/70">{value}</span>
              </div>
            ))}
          </div>
          <div className="mt-6 rounded-xl border border-white/8 bg-white/[0.025] p-4">
            <p className="text-[10px] uppercase tracking-[0.18em] text-white/25">Current workspace</p>
            <p className="mt-2 truncate text-sm">{activeWorkspace?.name ?? "None"}</p>
            <p className="mt-1 text-xs text-white/30">
              Role: {activeWorkspace ? String(activeWorkspace.role) : "—"}
            </p>
          </div>
        </aside>
      </div>
    </main>
  );
}
