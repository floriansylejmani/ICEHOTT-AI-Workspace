import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { WorkflowPanel } from "@/components/workflows/workflow-panel";

function jsonResponse(status: number, body: unknown) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response;
}

const workflow = {
  id: "workflow-1",
  name: "Release workflow",
  description: "Approval and artifact handoff",
  status: "Active",
  minimumRunRole: "Member",
  createdAtUtc: "2026-09-26T18:00:00Z",
  updatedAtUtc: "2026-09-26T18:10:00Z",
};

const version = {
  id: "version-1",
  versionNumber: 1,
  definitionHash: "a".repeat(64),
  status: "Active",
  createdAtUtc: "2026-09-26T18:00:00Z",
  activatedAtUtc: "2026-09-26T18:01:00Z",
  retiredAtUtc: null,
};

const run = {
  id: "run-1",
  workflowDefinitionId: "workflow-1",
  workflowVersionId: "version-1",
  status: "Waiting",
  currentStepKey: "approval",
  waitReason: "Checkpoint",
  createdAtUtc: "2026-09-26T18:15:00Z",
  startedAtUtc: "2026-09-26T18:15:01Z",
  completedAtUtc: null,
  resumeAtUtc: null,
  errorCode: null,
  cancellationRequestedAtUtc: null,
};

const checkpoint = {
  id: "checkpoint-1",
  workflowRunId: "run-1",
  stepRunId: "step-1",
  requestedByUserId: "user-1",
  minimumApproverRole: "Admin",
  requiresDifferentApprover: true,
  status: "Pending",
  decidedByUserId: null,
  createdAtUtc: "2026-09-26T18:15:02Z",
  decidedAtUtc: null,
  reason: null,
};

describe("WorkflowPanel", () => {
  afterEach(() => {
    cleanup();
  });

  it("renders a durable run and sends checkpoint and trigger actions to the API", async () => {
    const apiFetch = vi.fn(async (path: string, init?: RequestInit) => {
      if (path === "/api/workspaces/workspace-1/workflows?limit=100") {
        return jsonResponse(200, [workflow]);
      }
      if (path === "/api/workspaces/workspace-1/workflow-runs?limit=100") {
        return jsonResponse(200, [run]);
      }
      if (path === "/api/workspaces/workspace-1/workflows/workflow-1") {
        return jsonResponse(200, { ...workflow, versions: [version] });
      }
      if (path === "/api/workspaces/workspace-1/workflows/workflow-1/triggers" && !init?.method) {
        return jsonResponse(200, []);
      }
      if (path === "/api/workspaces/workspace-1/workflow-runs/run-1") {
        return jsonResponse(200, {
          ...run,
          steps: [
            {
              id: "step-1",
              stepKey: "approval",
              attempt: 1,
              stepType: "Checkpoint",
              status: "WaitingForCheckpoint",
              toolExecutionId: null,
              startedAtUtc: "2026-09-26T18:15:01Z",
              completedAtUtc: null,
              nextAttemptAtUtc: null,
              errorCode: null,
            },
          ],
        });
      }
      if (path === "/api/workspaces/workspace-1/workflow-runs/run-1/checkpoints?limit=100") {
        return jsonResponse(200, [checkpoint]);
      }
      if (
        path ===
        "/api/workspaces/workspace-1/workflow-runs/run-1/checkpoints/checkpoint-1/approve"
      ) {
        return jsonResponse(200, { ...checkpoint, status: "Approved" });
      }
      if (
        path === "/api/workspaces/workspace-1/workflows/workflow-1/triggers" &&
        init?.method === "POST"
      ) {
        return jsonResponse(201, {
          id: "trigger-1",
          workflowDefinitionId: "workflow-1",
          workflowVersionId: "version-1",
          type: "Schedule",
          scheduleExpression: "0 9 * * 1-5",
          timeZoneId: "UTC",
          runAsUserId: "user-1",
          enabled: true,
          nextRunAtUtc: "2026-09-27T09:00:00Z",
          lastRunAtUtc: null,
          createdByUserId: "user-1",
          createdAtUtc: "2026-09-26T18:20:00Z",
        });
      }
      throw new Error(`Unexpected request: ${init?.method ?? "GET"} ${path}`);
    });

    render(
      <WorkflowPanel
        workspaceId="workspace-1"
        workspaceRole="Owner"
        apiFetch={apiFetch}
      />,
    );

    expect(await screen.findByText("Release workflow")).toBeInTheDocument();
    expect(await screen.findByText("Run timeline")).toBeInTheDocument();
    expect(await screen.findByText("WaitingForCheckpoint")).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Checkpoint decision reason"), {
      target: { value: "Reviewed by operator" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => {
      expect(apiFetch).toHaveBeenCalledWith(
        "/api/workspaces/workspace-1/workflow-runs/run-1/checkpoints/checkpoint-1/approve",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify({ reason: "Reviewed by operator" }),
        }),
      );
    });

    fireEvent.change(screen.getByLabelText("Schedule timezone"), {
      target: { value: "UTC" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add schedule" }));

    await waitFor(() => {
      const createCall = apiFetch.mock.calls.find(
        ([path, init]) =>
          path === "/api/workspaces/workspace-1/workflows/workflow-1/triggers" &&
          (init as RequestInit | undefined)?.method === "POST",
      );
      expect(createCall).toBeTruthy();
      expect(JSON.parse(String((createCall?.[1] as RequestInit).body))).toEqual({
        workflowVersionId: "version-1",
        scheduleExpression: "0 9 * * 1-5",
        timeZoneId: "UTC",
      });
    });
  });

  it("does not render trigger administration for a Member", async () => {
    const apiFetch = vi.fn(async (path: string) => {
      if (path.endsWith("/workflows?limit=100")) return jsonResponse(200, [workflow]);
      if (path.endsWith("/workflow-runs?limit=100")) return jsonResponse(200, []);
      if (path.endsWith("/workflows/workflow-1")) {
        return jsonResponse(200, { ...workflow, versions: [version] });
      }
      if (path.endsWith("/workflows/workflow-1/triggers")) return jsonResponse(200, []);
      throw new Error(`Unexpected request: ${path}`);
    });

    render(
      <WorkflowPanel
        workspaceId="workspace-1"
        workspaceRole="Member"
        apiFetch={apiFetch}
      />,
    );

    expect(await screen.findByText("Release workflow")).toBeInTheDocument();
    expect(
      await screen.findByText(/Trigger administration requires Admin or Owner/),
    ).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add schedule" })).not.toBeInTheDocument();
  });
});
