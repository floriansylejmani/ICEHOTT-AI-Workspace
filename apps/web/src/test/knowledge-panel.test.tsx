import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { KnowledgePanel } from "@/components/knowledge/knowledge-panel";

function jsonResponse(status: number, body: object) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response;
}

const document = {
  id: "document-1",
  title: "Support Policy",
  sourceName: "support-policy.txt",
  status: "Ready",
  chunkCount: 2,
  characterCount: 2400,
  createdAtUtc: "2026-09-24T10:00:00Z",
  indexedAtUtc: "2026-09-24T10:00:01Z",
};

describe("KnowledgePanel", () => {
  afterEach(() => {
    cleanup();
  });

  it("indexes knowledge and tests workspace retrieval", async () => {
    const apiFetch = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(200, []))
      .mockResolvedValueOnce(jsonResponse(200, document))
      .mockResolvedValueOnce(jsonResponse(200, [document]))
      .mockResolvedValueOnce(
        jsonResponse(200, [
          {
            chunkId: "chunk-1",
            documentId: "document-1",
            title: "Support Policy",
            sourceName: "support-policy.txt",
            content: "The support window is thirty days.",
            score: 0.91,
          },
        ]),
      );

    render(<KnowledgePanel workspaceId="workspace-1" apiFetch={apiFetch} />);
    await waitFor(() => expect(apiFetch).toHaveBeenCalledTimes(1));

    fireEvent.change(screen.getByLabelText("Knowledge title"), {
      target: { value: "Support Policy" },
    });
    fireEvent.change(screen.getByLabelText("Source name"), {
      target: { value: "support-policy.txt" },
    });
    fireEvent.change(screen.getByLabelText("Knowledge content"), {
      target: { value: "The support window is thirty days." },
    });
    fireEvent.click(screen.getByRole("button", { name: "Index knowledge" }));

    expect(await screen.findByText("2 chunks · 2400 chars · support-policy.txt")).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Search knowledge"), {
      target: { value: "support window" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));

    expect(await screen.findByText("The support window is thirty days.")).toBeInTheDocument();
    expect(await screen.findByText("91%")).toBeInTheDocument();
  });

  it("uploads a server-processed PDF with multipart form data", async () => {
    const apiFetch = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(200, []))
      .mockResolvedValueOnce(jsonResponse(200, document))
      .mockResolvedValueOnce(jsonResponse(200, [document]));

    render(<KnowledgePanel workspaceId="workspace-1" apiFetch={apiFetch} />);
    await waitFor(() => expect(apiFetch).toHaveBeenCalledTimes(1));

    const file = new File(["fake-pdf-bytes"], "support-policy.pdf", {
      type: "application/pdf",
    });
    fireEvent.change(screen.getByLabelText("Knowledge file"), {
      target: { files: [file] },
    });

    fireEvent.click(screen.getByRole("button", { name: "Index knowledge" }));

    expect(await screen.findByText("2 chunks · 2400 chars · support-policy.txt")).toBeInTheDocument();

    expect(apiFetch.mock.calls[1][0]).toBe(
      "/api/workspaces/workspace-1/knowledge/documents/upload",
    );
    const uploadInit = apiFetch.mock.calls[1][1] as RequestInit;
    expect(uploadInit.method).toBe("POST");
    expect(uploadInit.body).toBeInstanceOf(FormData);
    const form = uploadInit.body as FormData;
    expect(form.get("file")).toBe(file);
    expect(form.get("title")).toBe("support-policy");
  });

  it("deletes a workspace knowledge document", async () => {
    const apiFetch = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(200, [document]))
      .mockResolvedValueOnce(jsonResponse(204, {}));

    render(<KnowledgePanel workspaceId="workspace-1" apiFetch={apiFetch} />);

    expect(await screen.findByText("Support Policy")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));

    await waitFor(() => {
      expect(screen.queryByText("Support Policy")).not.toBeInTheDocument();
    });
    expect(apiFetch).toHaveBeenLastCalledWith(
      "/api/workspaces/workspace-1/knowledge/documents/document-1",
      { method: "DELETE" },
    );
  });
});
