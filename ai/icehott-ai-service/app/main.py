import hashlib
import math
import re
from typing import Literal

from fastapi import FastAPI
from pydantic import BaseModel, Field

EMBEDDING_DIMENSIONS = 64

app = FastAPI(title="ICEHOTT AI Service", version="0.3.0")


class ChatMessage(BaseModel):
    role: Literal["user", "assistant", "system"]
    content: str = Field(min_length=1, max_length=12000)


class ChatKnowledge(BaseModel):
    chunkId: str
    documentId: str
    title: str = Field(min_length=1, max_length=200)
    sourceName: str | None = None
    content: str = Field(min_length=1)
    score: float


class ChatRequest(BaseModel):
    workspaceId: str
    userId: str
    conversationId: str
    messages: list[ChatMessage] = Field(min_length=1, max_length=200)
    knowledge: list[ChatKnowledge] = Field(default_factory=list, max_length=10)


class ChatResponse(BaseModel):
    content: str
    provider: str
    model: str


class EmbeddingRequest(BaseModel):
    texts: list[str] = Field(min_length=1, max_length=64)


class EmbeddingResponse(BaseModel):
    dimensions: int
    embeddings: list[list[float]]


@app.get("/health", tags=["system"])
async def health() -> dict[str, str]:
    return {"status": "ok", "service": "icehott-ai"}


@app.get("/ready", tags=["system"])
async def ready() -> dict[str, str]:
    return {"status": "ready"}


@app.post("/v1/embeddings", response_model=EmbeddingResponse, tags=["knowledge"])
async def embeddings(request: EmbeddingRequest) -> EmbeddingResponse:
    vectors = [_local_embedding(text) for text in request.texts]
    return EmbeddingResponse(dimensions=EMBEDDING_DIMENSIONS, embeddings=vectors)


@app.post("/v1/chat", response_model=ChatResponse, tags=["agent"])
async def chat(request: ChatRequest) -> ChatResponse:
    last_user = next(
        (message.content for message in reversed(request.messages) if message.role == "user"),
        "",
    )

    if request.knowledge:
        top_matches = request.knowledge[:3]
        titles = list(dict.fromkeys(match.title for match in top_matches))
        excerpt = top_matches[0].content[:900]
        answer = (
            f"Based on your workspace knowledge ({', '.join(titles)}): "
            f"{excerpt}"
        )
    else:
        answer = (
            "ICEHOTT AI runtime is online. "
            f'I received your request: "{last_user}". '
            f"This conversation currently contains {len(request.messages)} stored message(s) of context. "
            "No workspace knowledge matched this request yet."
        )

    return ChatResponse(
        content=answer,
        provider="icehott-local",
        model="phase3-rag-baseline",
    )


def _local_embedding(text: str) -> list[float]:
    vector = [0.0] * EMBEDDING_DIMENSIONS
    tokens = re.findall(r"\w+", text.lower(), flags=re.UNICODE)

    for token in tokens:
        digest = hashlib.sha256(token.encode("utf-8")).digest()
        bucket = int.from_bytes(digest[:4], "big") % EMBEDDING_DIMENSIONS
        sign = 1.0 if digest[4] % 2 == 0 else -1.0
        vector[bucket] += sign

    magnitude = math.sqrt(sum(value * value for value in vector))
    if magnitude == 0:
        return vector

    return [value / magnitude for value in vector]
