import logging
import time

import pytest
from opentelemetry.trace import SpanKind
from fastapi import FastAPI
from fastapi.testclient import TestClient
from opentelemetry.sdk.trace.export import SimpleSpanProcessor, SpanExportResult
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter

from app.telemetry import (
    REDACTED,
    SanitizingSpanExporter,
    TelemetryConfigError,
    _SafeTelemetryLogFilter,
    build_resource,
    configure_telemetry,
    load_settings,
    sanitize_attributes,
)

SECRET = "otlp-secret-sentinel-4711"
TRACE_ID = "0af7651916cd43dd8448eb211c80319c"
PARENT_SPAN_ID = "b7ad6b7169203331"
TRACEPARENT = f"00-{TRACE_ID}-{PARENT_SPAN_ID}-01"


def env(**values: str) -> dict[str, str]:
    base = {"OBSERVABILITY_ENABLED": "true", "OBSERVABILITY_OTLP_ENDPOINT": "https://collector.example:4317"}
    base.update(values)
    return base


def test_disabled_by_default_requires_nothing() -> None:
    settings = load_settings({})
    assert settings.enabled is False
    assert configure_telemetry(FastAPI(), settings) is None


def test_defaults_when_enabled() -> None:
    settings = load_settings(env())
    assert settings.protocol == "grpc"
    assert settings.sampling_ratio == 0.1
    assert settings.timeout_seconds == 5
    assert settings.traces_enabled and settings.metrics_enabled and settings.logs_enabled


@pytest.mark.parametrize(
    "key,value",
    [
        ("OBSERVABILITY_OTLP_ENDPOINT", ""),
        ("OBSERVABILITY_OTLP_ENDPOINT", "not-a-url"),
        ("OBSERVABILITY_OTLP_ENDPOINT", "ftp://collector.example"),
        ("OBSERVABILITY_OTLP_ENDPOINT", "https://collector.example/?a=b"),
        ("OBSERVABILITY_OTLP_PROTOCOL", "udp"),
        ("OBSERVABILITY_TRACES_SAMPLING_RATIO", "1.5"),
        ("OBSERVABILITY_TRACES_SAMPLING_RATIO", "-0.1"),
        ("OBSERVABILITY_TRACES_SAMPLING_RATIO", "nan"),
        ("OBSERVABILITY_TRACES_SAMPLING_RATIO", "abc"),
        ("OBSERVABILITY_OTLP_TIMEOUT_SECONDS", "0"),
        ("OBSERVABILITY_OTLP_TIMEOUT_SECONDS", "999"),
        ("OBSERVABILITY_OTLP_HEADERS", "missing-equals"),
        ("OBSERVABILITY_SERVICE_NAME", "Bad Name!"),
        ("OBSERVABILITY_METRICS_ENABLED", "maybe"),
    ],
)
def test_invalid_configuration_is_rejected_by_name(key: str, value: str) -> None:
    with pytest.raises(TelemetryConfigError) as error:
        load_settings(env(**{key: value}))
    assert key in str(error.value)


def test_hosted_requires_https_unless_explicitly_allowed() -> None:
    http = env(
        OBSERVABILITY_OTLP_ENDPOINT="http://collector.internal:4318",
        DEPLOYMENT_TIER="production",
        RELEASE_GIT_SHA="0123456789abcdef0123456789abcdef01234567",
    )
    with pytest.raises(TelemetryConfigError):
        load_settings(http)
    assert load_settings({**http, "OBSERVABILITY_OTLP_ALLOW_INSECURE": "true"}).enabled
    assert load_settings(env(OBSERVABILITY_OTLP_ENDPOINT="http://collector.internal:4318")).enabled


@pytest.mark.parametrize("sha", ["", "abcdef0", "g" * 40, "a" * 39, "b" * 41])
def test_hosted_observability_requires_exact_40_character_git_sha(sha: str) -> None:
    with pytest.raises(TelemetryConfigError) as error:
        load_settings(
            env(
                DEPLOYMENT_TIER="staging",
                RELEASE_GIT_SHA=sha,
            )
        )
    assert "RELEASE_GIT_SHA" in str(error.value)
    assert sha not in str(error.value) if sha else True


def test_errors_never_contain_configured_values() -> None:
    with pytest.raises(TelemetryConfigError) as error:
        load_settings(
            env(
                OBSERVABILITY_OTLP_ENDPOINT=f"http://user:{SECRET}@collector.example/?k={SECRET}",
                OBSERVABILITY_OTLP_HEADERS=f"broken {SECRET}",
                OBSERVABILITY_TRACES_SAMPLING_RATIO=SECRET,
            )
        )
    assert SECRET not in str(error.value)
    assert "collector.example" not in str(error.value)


def test_headers_are_not_in_settings_repr() -> None:
    settings = load_settings(env(OBSERVABILITY_OTLP_HEADERS=f"authorization=Bearer {SECRET}"))
    assert SECRET not in repr(settings)


def test_resource_has_only_safe_attributes() -> None:
    settings = load_settings(
        env(DEPLOYMENT_TIER="staging", RELEASE_GIT_SHA="0123456789abcdef0123456789abcdef01234567")
    )
    attrs = dict(build_resource(settings).attributes)
    assert attrs["service.name"] == "icehott-ai"
    assert attrs["service.version"] == "0123456789abcdef0123456789abcdef01234567"
    assert attrs["deployment.environment"] == "staging"
    assert attrs["icehott.service.role"] == "Ai"
    assert attrs["icehott.application"] == "icehott-ai-workspace"
    assert not any("user" in key or "workspace.id" in key for key in attrs)


def test_sanitize_attributes_drops_payloads_and_masks_sensitive_names() -> None:
    clean = sanitize_attributes(
        {
            "db.statement": "SELECT 1",
            "url.query": "token=abc",
            "url.full": "https://example.test/path?token=abc",
            "url.original": "https://example.test/path?token=abc",
            "http.target": "/path?token=abc",
            "http.request.header.authorization": "Bearer x",
            "user.password": "hunter2",
            "prompt.text": "private prompt",
            "http.request.method": "POST",
        }
    )
    assert clean == {
        "user.password": REDACTED,
        "prompt.text": REDACTED,
        "http.request.method": "POST",
    }


def test_otlp_log_filter_replaces_arbitrary_message_and_custom_extras_without_mutating_console_record() -> None:
    record = logging.LogRecord(
        "icehott.test",
        logging.ERROR,
        __file__,
        1,
        "prompt=%s",
        (SECRET,),
        None,
    )
    record.api_key = SECRET
    original = record.getMessage()

    filtered = _SafeTelemetryLogFilter().filter(record)

    assert isinstance(filtered, logging.LogRecord)
    assert filtered is not record
    assert filtered.getMessage() == "application_log"
    assert SECRET not in repr(filtered.__dict__)
    assert record.getMessage() == original
    assert SECRET in original

    internal = logging.LogRecord(
        "opentelemetry.exporter",
        logging.WARNING,
        __file__,
        1,
        "internal %s",
        (SECRET,),
        None,
    )
    assert _SafeTelemetryLogFilter().filter(internal) is False


@pytest.fixture
def instrumented():
    """A fresh FastAPI app instrumented with an in-memory exporter (no network)."""
    app = FastAPI()
    seen: dict[str, str | None] = {}

    @app.get("/health")
    async def health():
        return {"status": "ok"}

    @app.post("/v1/echo")
    async def echo(payload: dict):
        logging.getLogger("icehott.test").info("handling echo")
        return {"ok": True}

    exporter = InMemorySpanExporter()
    handle = configure_telemetry(
        app,
        load_settings(env(OBSERVABILITY_TRACES_SAMPLING_RATIO="1", OBSERVABILITY_METRICS_ENABLED="false",
                          OBSERVABILITY_LOGS_ENABLED="false")),
        span_exporter=exporter,
    )
    assert handle is not None
    try:
        yield app, exporter, handle, seen
    finally:
        handle.shutdown()


def test_incoming_w3c_trace_context_is_continued(instrumented) -> None:
    app, exporter, handle, _ = instrumented
    with TestClient(app) as client:
        response = client.post("/v1/echo", json={"text": "secret prompt text"}, headers={"traceparent": TRACEPARENT})
    assert response.status_code == 200
    handle.tracer_provider.force_flush()

    spans = exporter.get_finished_spans()
    server = [s for s in spans if s.kind == SpanKind.SERVER]
    assert server, [s.name for s in spans]
    assert format(server[0].context.trace_id, "032x") == TRACE_ID
    assert format(server[0].parent.span_id, "016x") == PARENT_SPAN_ID
    assert server[0].resource.attributes["service.name"] == "icehott-ai"


def test_no_body_or_header_content_is_exported(instrumented) -> None:
    app, exporter, handle, _ = instrumented
    with TestClient(app) as client:
        client.post(
            "/v1/echo",
            json={"text": "secret prompt text"},
            headers={"authorization": "Bearer jwt-sentinel", "traceparent": TRACEPARENT},
        )
    handle.tracer_provider.force_flush()

    rendered = repr([dict(s.attributes) for s in exporter.get_finished_spans()])
    assert "secret prompt text" not in rendered
    assert "jwt-sentinel" not in rendered


def test_probe_endpoints_are_not_traced(instrumented) -> None:
    app, exporter, handle, _ = instrumented
    with TestClient(app) as client:
        assert client.get("/health").status_code == 200
    handle.tracer_provider.force_flush()
    assert exporter.get_finished_spans() == ()


def test_logs_carry_trace_and_span_ids(instrumented, caplog) -> None:
    app, _, _, _ = instrumented
    with caplog.at_level(logging.INFO, logger="icehott.test"):
        with TestClient(app) as client:
            client.post("/v1/echo", json={}, headers={"traceparent": TRACEPARENT})
    records = [r for r in caplog.records if r.name == "icehott.test"]
    assert records
    assert records[0].otelTraceID == TRACE_ID
    assert records[0].otelSpanID not in ("0", "0" * 16)


def test_sanitizing_exporter_exports_clean_copies() -> None:
    inner = InMemorySpanExporter()
    from opentelemetry.sdk.trace import TracerProvider

    provider = TracerProvider()
    provider.add_span_processor(SimpleSpanProcessor(SanitizingSpanExporter(inner)))
    tracer = provider.get_tracer("test")
    with tracer.start_as_current_span("op") as span:
        span.set_attribute("db.statement", "SELECT secret")
        span.set_attribute("api_key", "sk-live")
        span.set_attribute("http.request.method", "GET")
        try:
            raise RuntimeError("Password=hunter2")
        except RuntimeError as exc:
            span.record_exception(exc)

    (exported,) = inner.get_finished_spans()
    assert "db.statement" not in exported.attributes
    assert exported.attributes["api_key"] == REDACTED
    assert exported.attributes["http.request.method"] == "GET"
    assert "hunter2" not in repr([dict(e.attributes) for e in exported.events])


def test_unreachable_collector_does_not_break_requests_or_shutdown() -> None:
    app = FastAPI()

    @app.get("/v1/ping")
    async def ping():
        return {"pong": True}

    # Port 9 (discard) refuses connections on loopback: a dead collector.
    handle = configure_telemetry(
        app,
        load_settings(
            env(
                OBSERVABILITY_OTLP_ENDPOINT="http://127.0.0.1:9",
                OBSERVABILITY_OTLP_PROTOCOL="http/protobuf",
                OBSERVABILITY_OTLP_TIMEOUT_SECONDS="1",
                OBSERVABILITY_TRACES_SAMPLING_RATIO="1",
            )
        ),
    )
    assert handle is not None
    try:
        started = time.monotonic()
        with TestClient(app) as client:
            for _ in range(20):
                assert client.get("/v1/ping").status_code == 200
        assert time.monotonic() - started < 5  # requests never wait on the exporter
    finally:
        shutdown_started = time.monotonic()
        handle.shutdown()
    assert time.monotonic() - shutdown_started < 30
