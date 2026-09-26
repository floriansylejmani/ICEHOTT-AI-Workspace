"use client";

import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";

type ApiFetch = (path: string, init?: RequestInit) => Promise<Response>;
type WorkspaceRole = number | string | null;

type WorkflowDefinition = {
  id: string;
  name: string;
  description: string | null;
  status: string;
  minimumRunRole: string;
  createdAtUtc: string;
  updatedAtUtc: string;
};

type WorkflowVersion = {
  id: string;
  versionNumber: number;
  definitionHash: string;
  status: string;
  createdAtUtc: string;
  activatedAtUtc: string | null;
  retiredAtUtc: string | null;
};

type WorkflowDetail = WorkflowDefinition & { versions: WorkflowVersion[] };

type WorkflowRun = {
  id: string;
  workflowDefinitionId: string;
  workflowVersionId: string;
  status: string;
  currentStepKey: string | null;
  waitReason: string | null;
  createdAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  resumeAtUtc: string | null;
  errorCode: string | null;
  cancellationRequestedAtUtc: string | null;
};

type WorkflowStep = {
  id: string;
  stepKey: string;
  attempt: number;
  stepType: string;
  status: string;
  toolExecutionId: string | null;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  nextAttemptAtUtc: string | null;
  errorCode: string | null;
};

type WorkflowRunDetail = WorkflowRun & { steps: WorkflowStep[] };

type Checkpoint = {
  id: string;
  workflowRunId: string;
  stepRunId: string;
  requestedByUserId: string;
  minimumApproverRole: string;
  requiresDifferentApprover: boolean;
  status: string;
  decidedByUserId: string | null;
  createdAtUtc: string;
  decidedAtUtc: string | null;
  reason: string | null;
};

type Trigger = {
  id: string;
  workflowDefinitionId: string;
  workflowVersionId: string;
  type: string;
  scheduleExpression: string;
  timeZoneId: string;
  runAsUserId: string;
  enabled: boolean;
  nextRunAtUtc: string;
  lastRunAtUtc: string | null;
  createdByUserId: string;
  createdAtUtc: string;
};

function formatDate(value: string | null) {
  if (!value) return "—";
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value));
}

function isAdmin(role: WorkspaceRole) {
  if (typeof role === "number") return role >= 2;
  const value = String(role ?? "").toLowerCase();
  return value === "admin" || value === "owner" || value === "2" || value === "3";
}

function statusClass(status: string) {
  const value = status.toLowerCase();
  if (value.includes("succeed") || value === "active" || value === "approved" || value === "ready") {
    return "border-emerald-300/20 bg-emerald-300/[0.07] text-emerald-200/80";
  }
  if (value.includes("fail") || value === "rejected" || value.includes("unknown")) {
    return "border-red-300/20 bg-red-300/[0.06] text-red-200/80";
  }
  if (value.includes("wait") || value === "pending" || value === "queued") {
    return "border-amber-200/20 bg-amber-200/[0.06] text-amber-100/75";
  }
  return "border-white/10 bg-white/[0.04] text-white/55";
}

async function readApiError(response: Response) {
  try {
    const body = (await response.json()) as { code?: string };
    return body.code ?? `request_failed_${response.status}`;
  } catch {
    return `request_failed_${response.status}`;
  }
}

export function WorkflowPanel({
  workspaceId,
  workspaceRole,
  apiFetch,
}: {
  workspaceId: string | null;
  workspaceRole: WorkspaceRole;
  apiFetch: ApiFetch;
}) {
  const [definitions, setDefinitions] = useState<WorkflowDefinition[]>([]);
  const [runs, setRuns] = useState<WorkflowRun[]>([]);
  const [selectedWorkflowId, setSelectedWorkflowId] = useState<string | null>(null);
  const [selectedRunId, setSelectedRunId] = useState<string | null>(null);
  const [detail, setDetail] = useState<WorkflowDetail | null>(null);
  const [runDetail, setRunDetail] = useState<WorkflowRunDetail | null>(null);
  const [checkpoints, setCheckpoints] = useState<Checkpoint[]>([]);
  const [triggers, setTriggers] = useState<Trigger[]>([]);
  const [scheduleExpression, setScheduleExpression] = useState("0 9 * * 1-5");
  const [timeZoneId, setTimeZoneId] = useState(
    () => Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC",
  );
  const [selectedVersionId, setSelectedVersionId] = useState("");
  const [decisionReason, setDecisionReason] = useState("");
  const [loading, setLoading] = useState(false);
  const [mutating, setMutating] = useState("");
  const [error, setError] = useState("");

  const canManageTriggers = isAdmin(workspaceRole);

  const loadWorkspace = useCallback(async () => {
    setError("");
    if (!workspaceId) {
      setDefinitions([]);
      setRuns([]);
      setSelectedWorkflowId(null);
      setSelectedRunId(null);
      return;
    }

    setLoading(true);
    try {
      const [definitionsResponse, runsResponse] = await Promise.all([
        apiFetch(`/api/workspaces/${workspaceId}/workflows?limit=100`),
        apiFetch(`/api/workspaces/${workspaceId}/workflow-runs?limit=100`),
      ]);
      if (!definitionsResponse.ok) throw new Error(await readApiError(definitionsResponse));
      if (!runsResponse.ok) throw new Error(await readApiError(runsResponse));

      const nextDefinitions = (await definitionsResponse.json()) as WorkflowDefinition[];
      const nextRuns = (await runsResponse.json()) as WorkflowRun[];
      setDefinitions(nextDefinitions);
      setRuns(nextRuns);
      setSelectedWorkflowId((current) =>
        current && nextDefinitions.some((item) => item.id === current)
          ? current
          : (nextDefinitions[0]?.id ?? null),
      );
      setSelectedRunId((current) =>
        current && nextRuns.some((item) => item.id === current)
          ? current
          : (nextRuns[0]?.id ?? null),
      );
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "workflow_load_failed");
    } finally {
      setLoading(false);
    }
  }, [workspaceId, apiFetch]);

  const loadWorkflowDetail = useCallback(async () => {
    if (!workspaceId || !selectedWorkflowId) {
      setDetail(null);
      setTriggers([]);
      return;
    }

    try {
      const [detailResponse, triggerResponse] = await Promise.all([
        apiFetch(`/api/workspaces/${workspaceId}/workflows/${selectedWorkflowId}`),
        apiFetch(`/api/workspaces/${workspaceId}/workflows/${selectedWorkflowId}/triggers`),
      ]);
      if (!detailResponse.ok) throw new Error(await readApiError(detailResponse));
      if (!triggerResponse.ok) throw new Error(await readApiError(triggerResponse));

      const nextDetail = (await detailResponse.json()) as WorkflowDetail;
      setDetail(nextDetail);
      setTriggers((await triggerResponse.json()) as Trigger[]);
      setSelectedVersionId((current) => {
        if (current && nextDetail.versions.some((version) => version.id === current)) return current;
        return nextDetail.versions.find((version) => version.status === "Active")?.id
          ?? nextDetail.versions[0]?.id
          ?? "";
      });
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "workflow_detail_failed");
    }
  }, [workspaceId, selectedWorkflowId, apiFetch]);

  const loadRunDetail = useCallback(async () => {
    if (!workspaceId || !selectedRunId) {
      setRunDetail(null);
      setCheckpoints([]);
      return;
    }

    try {
      const [runResponse, checkpointResponse] = await Promise.all([
        apiFetch(`/api/workspaces/${workspaceId}/workflow-runs/${selectedRunId}`),
        apiFetch(`/api/workspaces/${workspaceId}/workflow-runs/${selectedRunId}/checkpoints?limit=100`),
      ]);
      if (!runResponse.ok) throw new Error(await readApiError(runResponse));
      if (!checkpointResponse.ok) throw new Error(await readApiError(checkpointResponse));
      setRunDetail((await runResponse.json()) as WorkflowRunDetail);
      setCheckpoints((await checkpointResponse.json()) as Checkpoint[]);
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "workflow_run_failed");
    }
  }, [workspaceId, selectedRunId, apiFetch]);

  useEffect(() => {
    if (!workspaceId) return;
    let cancelled = false;

    async function bootstrap() {
      try {
        const [definitionsResponse, runsResponse] = await Promise.all([
          apiFetch(`/api/workspaces/${workspaceId}/workflows?limit=100`),
          apiFetch(`/api/workspaces/${workspaceId}/workflow-runs?limit=100`),
        ]);
        if (!definitionsResponse.ok) throw new Error(await readApiError(definitionsResponse));
        if (!runsResponse.ok) throw new Error(await readApiError(runsResponse));
        const nextDefinitions = (await definitionsResponse.json()) as WorkflowDefinition[];
        const nextRuns = (await runsResponse.json()) as WorkflowRun[];
        if (cancelled) return;

        const nextWorkflowId = nextDefinitions[0]?.id ?? null;
        setDefinitions(nextDefinitions);
        setRuns(nextRuns);
        setSelectedWorkflowId(nextWorkflowId);
        setSelectedRunId(
          nextRuns.find((run) => run.workflowDefinitionId === nextWorkflowId)?.id ?? null,
        );
      } catch (nextError) {
        if (!cancelled) {
          setError(nextError instanceof Error ? nextError.message : "workflow_load_failed");
        }
      }
    }

    void bootstrap();
    return () => { cancelled = true; };
  }, [workspaceId, apiFetch]);

  useEffect(() => {
    if (!workspaceId || !selectedWorkflowId) return;
    let cancelled = false;

    async function bootstrap() {
      try {
        const [detailResponse, triggerResponse] = await Promise.all([
          apiFetch(`/api/workspaces/${workspaceId}/workflows/${selectedWorkflowId}`),
          apiFetch(`/api/workspaces/${workspaceId}/workflows/${selectedWorkflowId}/triggers`),
        ]);
        if (!detailResponse.ok) throw new Error(await readApiError(detailResponse));
        if (!triggerResponse.ok) throw new Error(await readApiError(triggerResponse));
        const nextDetail = (await detailResponse.json()) as WorkflowDetail;
        const nextTriggers = (await triggerResponse.json()) as Trigger[];
        if (cancelled) return;

        setDetail(nextDetail);
        setTriggers(nextTriggers);
        setSelectedVersionId(
          nextDetail.versions.find((version) => version.status === "Active")?.id
            ?? nextDetail.versions[0]?.id
            ?? "",
        );
      } catch (nextError) {
        if (!cancelled) {
          setError(nextError instanceof Error ? nextError.message : "workflow_detail_failed");
        }
      }
    }

    void bootstrap();
    return () => { cancelled = true; };
  }, [workspaceId, selectedWorkflowId, apiFetch]);

  const workflowRuns = useMemo(
    () => selectedWorkflowId
      ? runs.filter((run) => run.workflowDefinitionId === selectedWorkflowId)
      : [],
    [runs, selectedWorkflowId],
  );

  useEffect(() => {
    if (!workspaceId || !selectedRunId) return;
    let cancelled = false;

    async function bootstrap() {
      try {
        const [runResponse, checkpointResponse] = await Promise.all([
          apiFetch(`/api/workspaces/${workspaceId}/workflow-runs/${selectedRunId}`),
          apiFetch(`/api/workspaces/${workspaceId}/workflow-runs/${selectedRunId}/checkpoints?limit=100`),
        ]);
        if (!runResponse.ok) throw new Error(await readApiError(runResponse));
        if (!checkpointResponse.ok) throw new Error(await readApiError(checkpointResponse));
        const nextRun = (await runResponse.json()) as WorkflowRunDetail;
        const nextCheckpoints = (await checkpointResponse.json()) as Checkpoint[];
        if (cancelled) return;
        setRunDetail(nextRun);
        setCheckpoints(nextCheckpoints);
      } catch (nextError) {
        if (!cancelled) {
          setError(nextError instanceof Error ? nextError.message : "workflow_run_failed");
        }
      }
    }

    void bootstrap();
    return () => { cancelled = true; };
  }, [workspaceId, selectedRunId, apiFetch]);

  function selectWorkflow(workflowId: string) {
    setSelectedWorkflowId(workflowId);
    setDetail(null);
    setTriggers([]);
    setRunDetail(null);
    setCheckpoints([]);
    setSelectedRunId(
      runs.find((run) => run.workflowDefinitionId === workflowId)?.id ?? null,
    );
  }

  async function createTrigger(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!workspaceId || !selectedWorkflowId || !selectedVersionId || mutating) return;
    setMutating("trigger-create");
    setError("");
    try {
      const response = await apiFetch(
        `/api/workspaces/${workspaceId}/workflows/${selectedWorkflowId}/triggers`,
        {
          method: "POST",
          body: JSON.stringify({
            workflowVersionId: selectedVersionId,
            scheduleExpression: scheduleExpression.trim(),
            timeZoneId: timeZoneId.trim(),
          }),
        },
      );
      if (!response.ok) throw new Error(await readApiError(response));
      await loadWorkflowDetail();
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "trigger_create_failed");
    } finally {
      setMutating("");
    }
  }

  async function toggleTrigger(trigger: Trigger) {
    if (!workspaceId || !selectedWorkflowId || mutating) return;
    setMutating(trigger.id);
    setError("");
    try {
      const action = trigger.enabled ? "disable" : "enable";
      const response = await apiFetch(
        `/api/workspaces/${workspaceId}/workflows/${selectedWorkflowId}/triggers/${trigger.id}/${action}`,
        { method: "POST" },
      );
      if (!response.ok) throw new Error(await readApiError(response));
      await loadWorkflowDetail();
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "trigger_update_failed");
    } finally {
      setMutating("");
    }
  }

  async function decideCheckpoint(checkpoint: Checkpoint, action: "approve" | "reject") {
    if (!workspaceId || !selectedRunId || mutating) return;
    setMutating(`${checkpoint.id}-${action}`);
    setError("");
    try {
      const response = await apiFetch(
        `/api/workspaces/${workspaceId}/workflow-runs/${selectedRunId}/checkpoints/${checkpoint.id}/${action}`,
        {
          method: "POST",
          body: JSON.stringify({ reason: decisionReason.trim() || null }),
        },
      );
      if (!response.ok) throw new Error(await readApiError(response));
      setDecisionReason("");
      await Promise.all([loadRunDetail(), loadWorkspace()]);
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "checkpoint_decision_failed");
    } finally {
      setMutating("");
    }
  }

  if (!workspaceId) {
    return (
      <section className="rounded-[26px] border border-white/10 bg-white/[0.035] p-8 text-sm text-white/35">
        Create or select a workspace to inspect workflows.
      </section>
    );
  }

  return (
    <section className="space-y-5">
      <div className="rounded-[26px] border border-white/10 bg-white/[0.035] p-6 sm:p-8">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <p className="text-xs uppercase tracking-[0.22em] text-amber-200/60">Durable automation</p>
            <h2 className="mt-2 text-3xl font-medium">Workflows & runs</h2>
            <p className="mt-2 max-w-2xl text-sm leading-6 text-white/35">
              Pinned versions, scheduled triggers, live run state, human checkpoints, and durable step history.
            </p>
          </div>
          <button
            type="button"
            onClick={() => void loadWorkspace()}
            disabled={loading}
            className="rounded-xl border border-white/10 px-4 py-2 text-xs text-white/55 hover:text-white disabled:opacity-40"
          >
            {loading ? "Refreshing…" : "Refresh"}
          </button>
        </div>

        {error && (
          <div role="alert" className="mt-5 rounded-xl border border-red-300/15 bg-red-300/[0.05] px-4 py-3 text-xs text-red-100/80">
            {error}
          </div>
        )}

        <div className="mt-7 grid gap-5 xl:grid-cols-[290px_1fr]">
          <aside className="rounded-2xl border border-white/8 bg-black/20 p-3">
            <p className="px-2 pb-3 text-[10px] uppercase tracking-[0.2em] text-white/25">Workflow definitions</p>
            <div className="space-y-2">
              {definitions.map((workflow) => {
                const selected = workflow.id === selectedWorkflowId;
                return (
                  <button
                    type="button"
                    key={workflow.id}
                    onClick={() => selectWorkflow(workflow.id)}
                    className={"w-full rounded-xl border px-3 py-3 text-left transition " +
                      (selected
                        ? "border-amber-200/25 bg-amber-100/[0.07]"
                        : "border-white/8 hover:border-white/15 hover:bg-white/[0.03]")}
                  >
                    <div className="flex items-start justify-between gap-3">
                      <span className="text-sm font-medium">{workflow.name}</span>
                      <span className={"rounded-full border px-2 py-0.5 text-[9px] " + statusClass(workflow.status)}>
                        {workflow.status}
                      </span>
                    </div>
                    <p className="mt-2 line-clamp-2 text-xs leading-5 text-white/30">
                      {workflow.description ?? "No description"}
                    </p>
                  </button>
                );
              })}
              {!loading && definitions.length === 0 && (
                <p className="px-2 py-6 text-sm text-white/30">No workflows in this workspace yet.</p>
              )}
            </div>
          </aside>

          <div className="min-w-0 space-y-5">
            {detail ? (
              <>
                <div className="rounded-2xl border border-white/8 bg-black/20 p-5">
                  <div className="flex flex-wrap items-start justify-between gap-4">
                    <div>
                      <div className="flex flex-wrap items-center gap-2">
                        <h3 className="text-xl font-medium">{detail.name}</h3>
                        <span className={"rounded-full border px-2.5 py-1 text-[10px] " + statusClass(detail.status)}>
                          {detail.status}
                        </span>
                      </div>
                      <p className="mt-2 max-w-2xl text-sm text-white/35">{detail.description ?? "No description"}</p>
                    </div>
                    <div className="text-right text-[11px] text-white/25">
                      <p>Minimum run role: {detail.minimumRunRole}</p>
                      <p className="mt-1">Updated {formatDate(detail.updatedAtUtc)}</p>
                    </div>
                  </div>

                  <div className="mt-5 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
                    {detail.versions.map((version) => (
                      <div key={version.id} className="rounded-xl border border-white/8 bg-white/[0.025] p-3">
                        <div className="flex items-center justify-between gap-2">
                          <span className="text-sm">Version {version.versionNumber}</span>
                          <span className={"rounded-full border px-2 py-0.5 text-[9px] " + statusClass(version.status)}>
                            {version.status}
                          </span>
                        </div>
                        <p className="mt-2 truncate font-mono text-[10px] text-white/25">
                          {version.definitionHash.slice(0, 16)}…
                        </p>
                      </div>
                    ))}
                  </div>
                </div>

                <div className="rounded-2xl border border-white/8 bg-black/20 p-5">
                  <div className="flex items-center justify-between gap-4">
                    <div>
                      <p className="text-sm font-medium">Scheduled triggers</p>
                      <p className="mt-1 text-xs text-white/30">Run-as identity and authorization stay server-authoritative.</p>
                    </div>
                    <span className="text-xs text-white/25">{triggers.length} configured</span>
                  </div>

                  <div className="mt-4 space-y-2">
                    {triggers.map((trigger) => (
                      <div key={trigger.id} className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-white/8 p-3">
                        <div>
                          <div className="flex items-center gap-2">
                            <span className={"rounded-full border px-2 py-0.5 text-[9px] " + statusClass(trigger.enabled ? "Active" : "Disabled")}>
                              {trigger.enabled ? "Enabled" : "Disabled"}
                            </span>
                            <span className="font-mono text-xs text-white/60">{trigger.scheduleExpression}</span>
                            <span className="text-xs text-white/30">{trigger.timeZoneId}</span>
                          </div>
                          <p className="mt-2 text-[11px] text-white/25">
                            Next {formatDate(trigger.nextRunAtUtc)} · Last {formatDate(trigger.lastRunAtUtc)}
                          </p>
                        </div>
                        {canManageTriggers && (
                          <button
                            type="button"
                            onClick={() => void toggleTrigger(trigger)}
                            disabled={Boolean(mutating)}
                            className="rounded-lg border border-white/10 px-3 py-2 text-xs text-white/55 hover:text-white disabled:opacity-40"
                          >
                            {trigger.enabled ? "Disable" : "Enable"}
                          </button>
                        )}
                      </div>
                    ))}
                    {triggers.length === 0 && (
                      <p className="rounded-xl border border-dashed border-white/10 p-4 text-sm text-white/30">No schedule configured.</p>
                    )}
                  </div>

                  {canManageTriggers ? (
                    <form onSubmit={createTrigger} className="mt-4 grid gap-3 border-t border-white/8 pt-4 lg:grid-cols-[1fr_1fr_1fr_auto]">
                      <select
                        aria-label="Workflow version"
                        value={selectedVersionId}
                        onChange={(event) => setSelectedVersionId(event.target.value)}
                        className="h-10 rounded-lg border border-white/10 bg-[#0d0e10] px-3 text-xs outline-none"
                      >
                        {detail.versions.map((version) => (
                          <option key={version.id} value={version.id}>v{version.versionNumber} · {version.status}</option>
                        ))}
                      </select>
                      <input
                        aria-label="Cron schedule"
                        value={scheduleExpression}
                        onChange={(event) => setScheduleExpression(event.target.value)}
                        className="h-10 rounded-lg border border-white/10 bg-black/20 px-3 font-mono text-xs outline-none"
                      />
                      <input
                        aria-label="Schedule timezone"
                        value={timeZoneId}
                        onChange={(event) => setTimeZoneId(event.target.value)}
                        className="h-10 rounded-lg border border-white/10 bg-black/20 px-3 text-xs outline-none"
                      />
                      <button
                        disabled={!selectedVersionId || Boolean(mutating)}
                        className="h-10 rounded-lg bg-white px-4 text-xs font-medium text-black disabled:opacity-40"
                      >
                        Add schedule
                      </button>
                    </form>
                  ) : (
                    <p className="mt-4 border-t border-white/8 pt-4 text-xs text-white/30">
                      Trigger administration requires Admin or Owner; the API enforces the same rule.
                    </p>
                  )}
                </div>

                <div className="grid gap-5 2xl:grid-cols-[300px_1fr]">
                  <div className="rounded-2xl border border-white/8 bg-black/20 p-3">
                    <p className="px-2 pb-3 text-[10px] uppercase tracking-[0.2em] text-white/25">Recent runs</p>
                    <div className="space-y-2">
                      {workflowRuns.map((run) => (
                        <button
                          type="button"
                          key={run.id}
                          onClick={() => setSelectedRunId(run.id)}
                          className={"w-full rounded-xl border px-3 py-3 text-left " +
                            (run.id === selectedRunId
                              ? "border-white/20 bg-white/[0.06]"
                              : "border-white/8 hover:border-white/15")}
                        >
                          <div className="flex items-center justify-between gap-2">
                            <span className={"rounded-full border px-2 py-0.5 text-[9px] " + statusClass(run.status)}>
                              {run.status}
                            </span>
                            <span className="text-[10px] text-white/25">{formatDate(run.createdAtUtc)}</span>
                          </div>
                          <p className="mt-2 truncate font-mono text-[10px] text-white/30">{run.id}</p>
                          {run.currentStepKey && <p className="mt-1 text-xs text-white/45">Step: {run.currentStepKey}</p>}
                        </button>
                      ))}
                      {workflowRuns.length === 0 && <p className="px-2 py-5 text-sm text-white/30">No runs yet.</p>}
                    </div>
                  </div>

                  <div className="rounded-2xl border border-white/8 bg-black/20 p-5">
                    {runDetail ? (
                      <>
                        <div className="flex flex-wrap items-start justify-between gap-3">
                          <div>
                            <div className="flex items-center gap-2">
                              <p className="text-sm font-medium">Run timeline</p>
                              <span className={"rounded-full border px-2 py-0.5 text-[9px] " + statusClass(runDetail.status)}>
                                {runDetail.status}
                              </span>
                            </div>
                            <p className="mt-2 font-mono text-[10px] text-white/25">{runDetail.id}</p>
                          </div>
                          <div className="text-right text-[11px] text-white/25">
                            <p>Started {formatDate(runDetail.startedAtUtc)}</p>
                            {runDetail.waitReason && <p className="mt-1">Waiting: {runDetail.waitReason}</p>}
                          </div>
                        </div>

                        <div className="mt-5 space-y-2">
                          {runDetail.steps.map((step, index) => (
                            <div key={step.id} className="grid grid-cols-[26px_1fr] gap-3">
                              <div className="flex flex-col items-center">
                                <span className={"mt-3 h-2.5 w-2.5 rounded-full border " + statusClass(step.status)} />
                                {index < runDetail.steps.length - 1 && <span className="mt-1 h-full min-h-8 w-px bg-white/10" />}
                              </div>
                              <div className="rounded-xl border border-white/8 p-3">
                                <div className="flex flex-wrap items-center justify-between gap-2">
                                  <div className="flex items-center gap-2">
                                    <span className="text-sm">{step.stepKey}</span>
                                    <span className="text-[10px] text-white/25">{step.stepType} · attempt {step.attempt}</span>
                                  </div>
                                  <span className={"rounded-full border px-2 py-0.5 text-[9px] " + statusClass(step.status)}>{step.status}</span>
                                </div>
                                <p className="mt-2 text-[10px] text-white/25">
                                  {formatDate(step.startedAtUtc)} → {formatDate(step.completedAtUtc)}
                                  {step.errorCode ? " · " + step.errorCode : ""}
                                </p>
                              </div>
                            </div>
                          ))}
                          {runDetail.steps.length === 0 && <p className="text-sm text-white/30">No persisted steps yet.</p>}
                        </div>

                        <div className="mt-6 border-t border-white/8 pt-5">
                          <div className="flex items-center justify-between gap-3">
                            <p className="text-sm font-medium">Human checkpoints</p>
                            <span className="text-xs text-white/25">
                              {checkpoints.filter((item) => item.status === "Pending").length} pending
                            </span>
                          </div>

                          {checkpoints.some((item) => item.status === "Pending") && (
                            <textarea
                              aria-label="Checkpoint decision reason"
                              value={decisionReason}
                              onChange={(event) => setDecisionReason(event.target.value)}
                              maxLength={500}
                              placeholder="Optional decision reason"
                              className="mt-3 min-h-20 w-full resize-y rounded-xl border border-white/10 bg-black/20 p-3 text-sm outline-none"
                            />
                          )}

                          <div className="mt-3 space-y-2">
                            {checkpoints.map((checkpoint) => (
                              <div key={checkpoint.id} className="rounded-xl border border-white/8 p-3">
                                <div className="flex flex-wrap items-center justify-between gap-3">
                                  <div>
                                    <div className="flex items-center gap-2">
                                      <span className={"rounded-full border px-2 py-0.5 text-[9px] " + statusClass(checkpoint.status)}>
                                        {checkpoint.status}
                                      </span>
                                      <span className="text-xs text-white/45">approver ≥ {checkpoint.minimumApproverRole}</span>
                                    </div>
                                    <p className="mt-2 text-[10px] text-white/25">
                                      Requested {formatDate(checkpoint.createdAtUtc)}
                                      {checkpoint.requiresDifferentApprover ? " · separation of duty" : ""}
                                    </p>
                                    {checkpoint.reason && <p className="mt-2 text-xs text-white/40">{checkpoint.reason}</p>}
                                  </div>
                                  {checkpoint.status === "Pending" && (
                                    <div className="flex gap-2">
                                      <button
                                        type="button"
                                        onClick={() => void decideCheckpoint(checkpoint, "reject")}
                                        disabled={Boolean(mutating)}
                                        className="rounded-lg border border-red-300/15 px-3 py-2 text-xs text-red-100/70 disabled:opacity-40"
                                      >
                                        Reject
                                      </button>
                                      <button
                                        type="button"
                                        onClick={() => void decideCheckpoint(checkpoint, "approve")}
                                        disabled={Boolean(mutating)}
                                        className="rounded-lg bg-white px-3 py-2 text-xs font-medium text-black disabled:opacity-40"
                                      >
                                        Approve
                                      </button>
                                    </div>
                                  )}
                                </div>
                              </div>
                            ))}
                            {checkpoints.length === 0 && <p className="text-sm text-white/30">No checkpoints for this run.</p>}
                          </div>
                        </div>
                      </>
                    ) : (
                      <p className="py-8 text-center text-sm text-white/30">Select a run to inspect its durable timeline.</p>
                    )}
                  </div>
                </div>
              </>
            ) : (
              <div className="rounded-2xl border border-dashed border-white/10 p-10 text-center text-sm text-white/30">
                Select a workflow to inspect its versions, triggers, runs, and checkpoints.
              </div>
            )}
          </div>
        </div>
      </div>
    </section>
  );
}
