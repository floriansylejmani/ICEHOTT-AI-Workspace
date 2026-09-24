from fastapi.testclient import TestClient

from app.main import app

client = TestClient(app)


def test_chat_returns_phase2_runtime_response() -> None:
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
    assert payload["model"] == "phase2-baseline-runtime"
    assert "Hello ICEHOTT" in payload["content"]
