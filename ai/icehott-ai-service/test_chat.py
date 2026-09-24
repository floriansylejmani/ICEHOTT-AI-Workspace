import math

from fastapi.testclient import TestClient

from app.main import app

client = TestClient(app)


def test_chat_returns_phase3_runtime_response_without_knowledge() -> None:
    response = client.post(
        "/v1/chat",
        json={
            "workspaceId": "workspace-1",
            "userId": "user-1",
            "conversationId": "conversation-1",
            "messages": [{"role": "user", "content": "Hello ICEHOTT"}],
        },
    )

    assert response.status_code == 200
    payload = response.json()
    assert payload["provider"] == "icehott-local"
    assert payload["model"] == "phase3-rag-baseline"
    assert "Hello ICEHOTT" in payload["content"]


def test_embeddings_are_64_dimensions_and_normalized() -> None:
    response = client.post(
        "/v1/embeddings",
        json={"texts": ["support policy thirty days", "another document"]},
    )

    assert response.status_code == 200
    payload = response.json()
    assert payload["dimensions"] == 64
    assert len(payload["embeddings"]) == 2
    assert all(len(vector) == 64 for vector in payload["embeddings"])
    assert math.isclose(
        math.sqrt(sum(value * value for value in payload["embeddings"][0])),
        1.0,
        rel_tol=1e-6,
    )


def test_related_embeddings_are_more_similar_than_unrelated_text() -> None:
    response = client.post(
        "/v1/embeddings",
        json={
            "texts": [
                "support window",
                "the support window is thirty days",
                "banana rocket ocean",
            ]
        },
    )

    vectors = response.json()["embeddings"]

    def dot(left: list[float], right: list[float]) -> float:
        return sum(a * b for a, b in zip(left, right, strict=True))

    assert dot(vectors[0], vectors[1]) > dot(vectors[0], vectors[2])


def test_chat_uses_retrieved_workspace_knowledge() -> None:
    response = client.post(
        "/v1/chat",
        json={
            "workspaceId": "workspace-1",
            "userId": "user-1",
            "conversationId": "conversation-1",
            "messages": [{"role": "user", "content": "What is the support window?"}],
            "knowledge": [
                {
                    "chunkId": "chunk-1",
                    "documentId": "document-1",
                    "title": "Support Policy",
                    "sourceName": "support-policy.txt",
                    "content": "The support window is thirty days.",
                    "score": 0.91,
                }
            ],
        },
    )

    assert response.status_code == 200
    payload = response.json()
    assert "Support Policy" in payload["content"]
    assert "thirty days" in payload["content"]
