"use client";

import { FormEvent, useEffect, useState } from "react";

type ApiFetch = (path: string, init?: RequestInit) => Promise<Response>;

type ConversationSummary = {
  id: string;
  title: string;
  createdAtUtc: string;
  updatedAtUtc: string;
};

type MessageView = {
  id: string;
  role: string;
  content: string;
  createdAtUtc: string;
};

type ConversationView = {
  id: string;
  workspaceId: string;
  title: string;
  messages: MessageView[];
};

type ChatReply = {
  conversationId: string;
  userMessage: MessageView;
  assistantMessage: MessageView;
  provider: string;
  model: string;
};

export function AgentPanel({
  workspaceId,
  apiFetch,
}: {
  workspaceId: string | null;
  apiFetch: ApiFetch;
}) {
  const [conversationId, setConversationId] = useState<string | null>(null);
  const [messages, setMessages] = useState<MessageView[]>([]);
  const [prompt, setPrompt] = useState("");
  const [loading, setLoading] = useState(false);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState("");
  const [runtime, setRuntime] = useState("");

  useEffect(() => {
    let cancelled = false;

    async function loadLatestConversation() {
      setConversationId(null);
      setMessages([]);
      setRuntime("");
      setError("");
      if (!workspaceId) return;

      setLoading(true);
      try {
        const listResponse = await apiFetch(
          "/api/workspaces/" + workspaceId + "/conversations",
        );
        if (!listResponse.ok) throw new Error("conversation_list_failed");

        const conversations = (await listResponse.json()) as ConversationSummary[];
        if (cancelled || conversations.length === 0) return;

        const latest = conversations[0];
        const conversationResponse = await apiFetch(
          "/api/workspaces/" + workspaceId + "/conversations/" + latest.id,
        );
        if (!conversationResponse.ok) throw new Error("conversation_load_failed");

        const conversation = (await conversationResponse.json()) as ConversationView;
        if (!cancelled) {
          setConversationId(conversation.id);
          setMessages(conversation.messages);
        }
      } catch {
        if (!cancelled) setError("Could not load AI conversation history.");
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    void loadLatestConversation();
    return () => {
      cancelled = true;
    };
  }, [workspaceId, apiFetch]);

  async function sendMessage(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const content = prompt.trim();
    if (!workspaceId || !content || sending) return;

    setSending(true);
    setError("");
    try {
      const response = await apiFetch(
        "/api/workspaces/" + workspaceId + "/conversations/chat",
        {
          method: "POST",
          body: JSON.stringify({ conversationId, content }),
        },
      );

      if (!response.ok) throw new Error("chat_failed");
      const reply = (await response.json()) as ChatReply;

      setConversationId(reply.conversationId);
      setMessages((current) => [
        ...current,
        reply.userMessage,
        reply.assistantMessage,
      ]);
      setRuntime(reply.provider + " · " + reply.model);
      setPrompt("");
    } catch {
      setError("ICEHOTT AI could not answer. Check the AI runtime service.");
    } finally {
      setSending(false);
    }
  }

  function startNewConversation() {
    setConversationId(null);
    setMessages([]);
    setRuntime("");
    setError("");
  }

  return (
    <section className="rounded-[26px] border border-white/10 bg-white/[0.035] p-6 sm:p-8">
      <div className="flex items-start justify-between gap-4">
        <div>
          <p className="text-sm text-white/40">ICEHOTT AI</p>
          <h2 className="mt-3 max-w-2xl text-3xl font-medium leading-tight">
            Agent runtime is live.
          </h2>
          <p className="mt-2 text-sm text-white/35">
            Workspace-scoped history is stored securely in PostgreSQL.
          </p>
        </div>
        {conversationId && (
          <button
            type="button"
            onClick={startNewConversation}
            className="rounded-lg border border-white/10 px-3 py-2 text-xs text-white/45 hover:text-white"
          >
            New chat
          </button>
        )}
      </div>

      <div className="mt-7 max-h-[360px] space-y-3 overflow-y-auto pr-1">
        {loading && <p className="text-sm text-white/30">Loading conversation…</p>}
        {!loading && messages.length === 0 && (
          <div className="rounded-2xl border border-white/8 bg-black/20 p-5 text-sm text-white/35">
            Ask ICEHOTT anything to start the first Phase 2 conversation.
          </div>
        )}
        {messages.map((message) => {
          const isUser = message.role.toLowerCase() === "user";
          return (
            <div
              key={message.id}
              className={
                "max-w-[88%] rounded-2xl px-4 py-3 text-sm leading-6 " +
                (isUser
                  ? "ml-auto bg-white text-black"
                  : "border border-white/10 bg-black/25 text-white/80")
              }
            >
              <p>{message.content}</p>
              <p className={isUser ? "mt-2 text-[10px] text-black/45" : "mt-2 text-[10px] text-white/25"}>
                {isUser ? "You" : "ICEHOTT AI"}
              </p>
            </div>
          );
        })}
      </div>

      <form onSubmit={sendMessage} className="mt-6">
        <div className="flex gap-3 rounded-2xl border border-white/10 bg-black/20 p-2 focus-within:border-amber-200/35">
          <input
            aria-label="Ask ICEHOTT"
            value={prompt}
            onChange={(event) => setPrompt(event.target.value)}
            placeholder="Ask ICEHOTT…"
            disabled={!workspaceId || sending}
            maxLength={12000}
            className="min-w-0 flex-1 bg-transparent px-3 text-sm text-white outline-none placeholder:text-white/25 disabled:opacity-50"
          />
          <button
            type="submit"
            disabled={!workspaceId || sending || !prompt.trim()}
            className="rounded-xl bg-white px-5 py-2.5 text-sm font-medium text-black hover:bg-amber-100 disabled:opacity-35"
          >
            {sending ? "Thinking…" : "Send"}
          </button>
        </div>
        <div className="mt-2 flex min-h-5 items-center justify-between gap-4 text-[11px]">
          <span className="text-red-200/70">{error}</span>
          <span className="text-white/25">{runtime || "Phase 2 runtime"}</span>
        </div>
      </form>
    </section>
  );
}
