import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useState } from "react";
import { AuthProvider, useAuth } from "@/lib/auth";

function Probe() {
  const { status, user, login, logout, apiFetch } = useAuth();
  const [loaded, setLoaded] = useState("idle");

  return (
    <div>
      <span>{status}{user ? `:${user.displayName}` : ""}</span>
      <span>{loaded}</span>
      <button onClick={() => void login("user@icehott.dev", "StrongPassword123!")}>login</button>
      <button onClick={() => void logout()}>logout</button>
      <button
        onClick={() => void apiFetch("/api/workspaces").then((result) => setLoaded(result.ok ? "loaded" : "failed"))}
      >
        load
      </button>
    </div>
  );
}

function response(status: number, body: object = {}) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response;
}

function authBody(accessToken: string) {
  return {
    user: { id: "u1", email: "user@icehott.dev", displayName: "Test User" },
    accessToken,
    accessTokenExpiresAtUtc: "2026-09-24T12:00:00Z",
  };
}

describe("AuthProvider", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("recovers from an anonymous refresh and authenticates in memory", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(401, { code: "missing_refresh_token" }))
      .mockResolvedValueOnce(response(200, authBody("access-token")));

    vi.stubGlobal("fetch", fetchMock);
    render(<AuthProvider><Probe /></AuthProvider>);

    expect(await screen.findByText("anonymous")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "login" }));
    expect(await screen.findByText("authenticated:Test User")).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("refreshes once and retries a protected API request after a 401", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(200, authBody("access-one")))
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(200, authBody("access-two")))
      .mockResolvedValueOnce(response(200, []));

    vi.stubGlobal("fetch", fetchMock);
    render(<AuthProvider><Probe /></AuthProvider>);

    expect(await screen.findByText("anonymous")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "login" }));
    expect(await screen.findByText("authenticated:Test User")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "load" }));
    expect(await screen.findByText("loaded")).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(5);

    const retryInit = fetchMock.mock.calls[4][1] as RequestInit;
    expect((retryInit.headers as Headers).get("Authorization")).toBe("Bearer access-two");
  });

  it("clears local authentication state after logout", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(200, authBody("access-token")))
      .mockResolvedValueOnce(response(204));

    vi.stubGlobal("fetch", fetchMock);
    render(<AuthProvider><Probe /></AuthProvider>);

    expect(await screen.findByText("anonymous")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "login" }));
    expect(await screen.findByText("authenticated:Test User")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "logout" }));
    expect(await screen.findByText("anonymous")).toBeInTheDocument();
  });
});
