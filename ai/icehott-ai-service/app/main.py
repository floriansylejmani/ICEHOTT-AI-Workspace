from typing import Literal

from fastapi import FastAPI
from pydantic import BaseModel, Field

app = FastAPI(title="ICEHOTT AI Service", version="0.2.0")


class ChatMessage(BaseModel):
    role: Literal["user", "assistant", "system"]
    content: str = Field(min_length=1, max_length=12000)


class ChatRequest(BaseModel):
    workspaceId: str
    userId: str
    conversationId: str
    messages: list[ChatMessage] = Field(min_length=1, max_length=200)


class ChatResponse(BaseModel):
    content: str
    provider: str
    model: str


@app.get("/health", tags=["system"])
async def health() -> dict[str, str]:
    return {"status": "ok", "service": "icehott-ai"}


@app.get("/ready", tags=["system"])
async def ready() -> dict[str, str]:
    return {"status": "ready"}


@app.post("/v1/chat", response_model=ChatResponse, tags=["agent"])
async def chat(request: ChatRequest) -> ChatResponse:
    last_user = next(
        (message.content for message in reversed(request.messages) if message.role == "user"),
        "",
    )

    answer = (
        "ICEHOTT AI runtime is online. "
        f'I received your request: "{last_user}". '
        f"This conversation currently contains {len(request.messages)} stored message(s) of context."
    )

    return ChatResponse(
        content=answer,
        provider="icehott-local",
        model="phase2-baseline-runtime",
    )
