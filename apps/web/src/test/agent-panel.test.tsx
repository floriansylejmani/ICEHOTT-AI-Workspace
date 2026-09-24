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

  it("sends the first workspace-scoped message and renders the AI reply", async () => {
    const apiFetch = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(200, []))
      .mockResolvedValueOnce(
        jsonResponse(200, {
          conversationId: "conversation-1",
          userMessage: {
            id: "message-user",
            role: "User",
            content: "Hello ICEHOTT",
            createdAtUtc: "2026-09-24T10:00:00Z",
          },
          assistantMessage: {
            id: "message-ai",
            role: "Assistant",
            content: "ICEHOTT AI runtime is online.",
            createdAtUtc: "2026-09-24T10:00:01Z",
          },
          provider: "icehott-local",
          model: "phase2-baseline-runtime",
        }),
      );

    render(<AgentPanel workspaceId="workspace-1" apiFetch={apiFetch} />);

    await waitFor(() => expect(apiFetch).toHaveBeenCalledTimes(1));
    fireEvent.change(screen.getByLabelText("Ask ICEHOTT"), {
      target: { value: "Hello ICEHOTT" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    expect(await screen.findByText("Hello ICEHOTT")).toBeInTheDocument();
    expect(await screen.findByText("ICEHOTT AI runtime is online.")).toBeInTheDocument();
    expect(await screen.findByText("icehott-local · phase2-baseline-runtime")).toBeInTheDocument();

    expect(apiFetch).toHaveBeenLastCalledWith(
      "/api/workspaces/workspace-1/conversations/chat",
      expect.objectContaining({ method: "POST" }),
    );
  });
});
