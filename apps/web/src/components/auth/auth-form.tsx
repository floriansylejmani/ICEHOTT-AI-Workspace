"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { FormEvent, useEffect, useState } from "react";
import { useAuth } from "@/lib/auth";

type Props = { mode: "login" | "register" };

export function AuthForm({ mode }: Props) {
  const router = useRouter();
  const { login, register, status } = useAuth();
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (status === "authenticated") router.replace("/app");
  }, [status, router]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      if (mode === "register") await register(email, displayName, password);
      else await login(email, password);
      router.replace("/app");
    } catch (err) {
      const code = err instanceof Error ? err.message : "request_failed";
      setError(code === "email_exists" ? "An account with this email already exists." : code === "invalid_credentials" ? "Email or password is incorrect." : "Something went wrong. Please try again.");
    } finally {
      setBusy(false);
    }
  }

  const isRegister = mode === "register";

  return (
    <div className="grid min-h-screen bg-[#08090b] text-white lg:grid-cols-[1.1fr_0.9fr]">
      <section className="hidden border-r border-white/10 p-12 lg:flex lg:flex-col lg:justify-between">
        <Link href="/" className="text-lg font-semibold tracking-[0.28em]">ICEHOTT</Link>
        <div className="max-w-xl space-y-6">
          <p className="text-xs uppercase tracking-[0.32em] text-amber-200/70">AI workspace for real work</p>
          <h1 className="text-5xl font-medium leading-tight">One secure workspace for knowledge, tools and AI execution.</h1>
          <p className="max-w-lg text-base leading-7 text-white/55">Built around grounded context, human approval and traceable agent actions.</p>
        </div>
        <p className="text-sm text-white/35">Phase 1 · Identity & Workspaces</p>
      </section>

      <section className="flex items-center justify-center px-6 py-12">
        <div className="w-full max-w-md">
          <Link href="/" className="mb-12 block text-sm font-semibold tracking-[0.24em] lg:hidden">ICEHOTT</Link>
          <p className="mb-3 text-sm text-amber-200/70">{isRegister ? "Create your workspace" : "Welcome back"}</p>
          <h2 className="mb-8 text-3xl font-semibold">{isRegister ? "Start with ICEHOTT" : "Sign in to continue"}</h2>

          <form onSubmit={submit} className="space-y-5">
            {isRegister && (
              <Field label="Display name" value={displayName} onChange={setDisplayName} autoComplete="name" minLength={2} />
            )}
            <Field label="Email" value={email} onChange={setEmail} type="email" autoComplete="email" />
            <Field label="Password" value={password} onChange={setPassword} type="password" autoComplete={isRegister ? "new-password" : "current-password"} minLength={isRegister ? 12 : undefined} />

            {error && <p role="alert" className="rounded-xl border border-red-400/20 bg-red-400/10 px-4 py-3 text-sm text-red-200">{error}</p>}

            <button
              disabled={busy}
              className="h-12 w-full rounded-xl bg-white font-medium text-black transition hover:bg-amber-100 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {busy ? "Please wait…" : isRegister ? "Create account" : "Sign in"}
            </button>
          </form>

          <p className="mt-7 text-sm text-white/45">
            {isRegister ? "Already have an account?" : "New to ICEHOTT?"}{" "}
            <Link className="text-white hover:text-amber-100" href={isRegister ? "/login" : "/register"}>
              {isRegister ? "Sign in" : "Create account"}
            </Link>
          </p>
        </div>
      </section>
    </div>
  );
}

function Field({ label, value, onChange, type = "text", autoComplete, minLength }: {
  label: string; value: string; onChange: (value: string) => void; type?: string; autoComplete?: string; minLength?: number;
}) {
  return (
    <label className="block space-y-2 text-sm text-white/65">
      <span>{label}</span>
      <input
        required
        type={type}
        value={value}
        minLength={minLength}
        autoComplete={autoComplete}
        onChange={(event) => onChange(event.target.value)}
        className="h-12 w-full rounded-xl border border-white/10 bg-white/[0.04] px-4 text-white outline-none transition placeholder:text-white/20 focus:border-amber-200/50 focus:bg-white/[0.06]"
      />
    </label>
  );
}
