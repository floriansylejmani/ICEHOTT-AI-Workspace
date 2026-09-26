import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ArtifactPanel } from "@/components/artifacts/artifact-panel";

function jsonResponse(status: number, body: unknown) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response;
}

const artifact = {
  id: "artifact-1",
  fileName: "report.pdf",
  contentType: "application/pdf",
  sizeBytes: 4096,
  sha256: "b".repeat(64),
  status: "Ready",
  createdByUserId: "user-1",
  workflowRunId: "run-1",
  stepRunId: "step-1",
  createdAtUtc: "2026-09-26T19:00:00Z",
  failedAtUtc: null,
  deletedAtUtc: null,
};

describe("ArtifactPanel", () => {
  afterEach(() => {
    cleanup();
  });

  it("uploads with multipart form data and deletes through workspace-scoped API", async () => {
    const apiFetch = vi.fn(async (path: string, init?: RequestInit) => {
      if (path === "/api/workspaces/workspace-1/artifacts?limit=100") {
        return jsonResponse(200, [artifact]);
      }
      if (path === "/api/workspaces/workspace-1/artifacts/upload") {
        return jsonResponse(201, artifact);
      }
      if (path === "/api/workspaces/workspace-1/artifacts/artifact-1" && init?.method === "DELETE") {
        return jsonResponse(204, {});
      }
      throw new Error(`Unexpected request: ${init?.method ?? "GET"} ${path}`);
    });

    render(<ArtifactPanel workspaceId="workspace-1" apiFetch={apiFetch} />);

    expect(await screen.findByText("report.pdf")).toBeInTheDocument();
    expect(screen.getByText("4.0 KB")).toBeInTheDocument();

    const uploadFile = new File(["artifact-content"], "evidence.txt", {
      type: "text/plain",
    });
    fireEvent.change(screen.getByLabelText("Artifact file"), {
      target: { files: [uploadFile] },
    });
    fireEvent.click(screen.getByRole("button", { name: "Upload artifact" }));

    await waitFor(() => {
      const uploadCall = apiFetch.mock.calls.find(
        ([path]) => path === "/api/workspaces/workspace-1/artifacts/upload",
      );
      expect(uploadCall).toBeTruthy();
      const init = uploadCall?.[1] as RequestInit;
      expect(init.method).toBe("POST");
      expect(init.body).toBeInstanceOf(FormData);
      const body = init.body as FormData;
      expect(body.get("file")).toBe(uploadFile);
      expect(String(body.get("idempotencyKey"))).not.toHaveLength(0);
    });

    fireEvent.click(screen.getByRole("button", { name: "Delete" }));

    await waitFor(() => {
      expect(apiFetch).toHaveBeenCalledWith(
        "/api/workspaces/workspace-1/artifacts/artifact-1",
        { method: "DELETE" },
      );
      expect(screen.queryByText("report.pdf")).not.toBeInTheDocument();
    });
  });
});
