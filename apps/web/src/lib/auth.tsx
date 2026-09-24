"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5050";

export type AuthUser = {
  id: string;
  email: string;
  displayName: string;
};

type AuthResponse = {
  user: AuthUser;
  accessToken: string;
  accessTokenExpiresAtUtc: string;
};

type AuthStatus = "loading" | "authenticated" | "anonymous";

type AuthContextValue = {
  user: AuthUser | null;
  status: AuthStatus;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, displayName: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  apiFetch: (path: string, init?: RequestInit) => Promise<Response>;
};

const AuthContext = createContext<AuthContextValue | null>(null);

async function readError(response: Response): Promise<string> {
  try {
    const body = (await response.json()) as { code?: string };
    return body.code ?? "request_failed";
  } catch {
    return "request_failed";
  }
}

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [accessToken, setAccessToken] = useState<string | null>(null);
  const [status, setStatus] = useState<AuthStatus>("loading");

  const clearAuth = useCallback(() => {
    setUser(null);
    setAccessToken(null);
    setStatus("anonymous");
  }, []);

  const applyAuth = useCallback((response: AuthResponse) => {
    setUser(response.user);
    setAccessToken(response.accessToken);
    setStatus("authenticated");
  }, []);

  const refreshSession = useCallback(async (): Promise<AuthResponse | null> => {
    const response = await fetch(`${API_URL}/api/auth/refresh`, {
      method: "POST",
      credentials: "include",
      headers: { "X-ICEHOTT-CSRF": "1" },
    });

    if (!response.ok) {
      clearAuth();
      return null;
    }

    const auth = (await response.json()) as AuthResponse;
    applyAuth(auth);
    return auth;
  }, [applyAuth, clearAuth]);

  useEffect(() => {
    let cancelled = false;

    async function bootstrap() {
      try {
        const response = await fetch(`${API_URL}/api/auth/refresh`, {
          method: "POST",
          credentials: "include",
          headers: { "X-ICEHOTT-CSRF": "1" },
        });

        if (!response.ok) throw new Error("refresh_failed");
        const auth = (await response.json()) as AuthResponse;
        if (!cancelled) applyAuth(auth);
      } catch {
        if (!cancelled) clearAuth();
      }
    }

    void bootstrap();
    return () => {
      cancelled = true;
    };
  }, [applyAuth, clearAuth]);

  const authenticate = useCallback(async (path: string, body: object) => {
    const response = await fetch(`${API_URL}${path}`, {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });

    if (!response.ok) throw new Error(await readError(response));
    applyAuth((await response.json()) as AuthResponse);
  }, [applyAuth]);

  const login = useCallback(
    (email: string, password: string) => authenticate("/api/auth/login", { email, password }),
    [authenticate],
  );

  const register = useCallback(
    (email: string, displayName: string, password: string) =>
      authenticate("/api/auth/register", { email, displayName, password }),
    [authenticate],
  );

  const logout = useCallback(async () => {
    try {
      await fetch(`${API_URL}/api/auth/logout`, {
        method: "POST",
        credentials: "include",
        headers: { "X-ICEHOTT-CSRF": "1" },
      });
    } finally {
      clearAuth();
    }
  }, [clearAuth]);

  const apiFetch = useCallback(
    async (path: string, init: RequestInit = {}) => {
      async function send(token: string | null) {
        const headers = new Headers(init.headers);
        if (token) headers.set("Authorization", `Bearer ${token}`);
        const isFormData =
          typeof FormData !== "undefined" && init.body instanceof FormData;
        if (init.body && !isFormData && !headers.has("Content-Type")) {
          headers.set("Content-Type", "application/json");
        }
        return fetch(`${API_URL}${path}`, { ...init, headers, credentials: "include" });
      }

      const response = await send(accessToken);
      if (response.status !== 401) return response;

      const refreshed = await refreshSession();
      if (!refreshed) return response;

      return send(refreshed.accessToken);
    },
    [accessToken, refreshSession],
  );

  const value = useMemo(
    () => ({ user, status, login, register, logout, apiFetch }),
    [user, status, login, register, logout, apiFetch],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error("useAuth must be used inside AuthProvider");
  return value;
}
