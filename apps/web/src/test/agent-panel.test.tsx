import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AgentPanel } from "@/components/agent/agent-panel";

function jsonResponse(status: number, body: object) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response;
}

describe("AgentPanel", () => {
  afterEach(() => {
    cleanup();
  });

  it("renders a workspace-scoped RAG reply with source citations", async () => {
    const apiFetch = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(200, []))
      .mockResolvedValueOnce(
        jsonResponse(200, {
          conversationId: "conversation-1",
          userMessage: {
            id: "message-user",
            role: "User",
            content: "What is the support window?",
            createdAtUtc: "2026-09-24T10:00:00Z",
            citations: [],
          },
          assistantMessage: {
            id: "message-ai",
            role: "Assistant",
            content: "The support window is thirty days.",
            createdAtUtc: "2026-09-24T10:00:01Z",
            citations: [
              {
                documentId: "document-1",
                chunkId: "chunk-1",
                title: "Support Policy",
                sourceName: "support-policy.txt",
                score: 0.91,
              },
            ],
          },
          provider: "icehott-local",
          model: "phase3-rag-baseline",
        }),
      );

    render(<AgentPanel workspaceId="workspace-1" apiFetch={apiFetch} />);

    await waitFor(() => expect(apiFetch).toHaveBeenCalledTimes(1));
    fireEvent.change(screen.getByLabelText("Ask ICEHOTT"), {
      target: { value: "What is the support window?" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    expect(await screen.findByText("The support window is thirty days.")).toBeInTheDocument();
    expect(await screen.findByText("Support Policy · 91%")).toBeInTheDocument();
    expect(await screen.findByText("icehott-local · phase3-rag-baseline")).toBeInTheDocument();

    expect(apiFetch).toHaveBeenLastCalledWith(
      "/api/workspaces/workspace-1/conversations/chat",
      expect.objectContaining({ method: "POST" }),
    );
  });
});
