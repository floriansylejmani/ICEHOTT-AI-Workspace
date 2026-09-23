from fastapi import FastAPI

app = FastAPI(title="ICEHOTT AI Service", version="0.1.0")


@app.get("/health", tags=["system"])
async def health() -> dict[str, str]:
    return {"status": "ok", "service": "icehott-ai"}


@app.get("/ready", tags=["system"])
async def ready() -> dict[str, str]:
    return {"status": "ready"}
