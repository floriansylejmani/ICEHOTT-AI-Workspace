"""OpenTelemetry setup for the ICEHOTT AI service.

Design rules (see docs/PHASE-7C-OBSERVABILITY-ALERTING.md):

* Telemetry is auxiliary. It is off unless ``OBSERVABILITY_ENABLED=true``; when enabled the
  configuration is validated fail-closed at startup, but a dead collector never affects requests.
* Exporting is asynchronous and bounded (batch processors with a short timeout).
* No request/response bodies, prompts, headers, SQL or exception text are exported.
* Validation messages name environment variables, never their values.
"""

from __future__ import annotations

import logging
import math
import os
import re
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from urllib.parse import urlparse

from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import ReadableSpan, TracerProvider
from opentelemetry.sdk.trace.export import (
    BatchSpanProcessor,
    SpanExporter,
    SpanExportResult,
)
from opentelemetry.sdk.trace.sampling import ParentBased, TraceIdRatioBased

SERVICE_NAME_DEFAULT = "icehott-ai"
APPLICATION_NAME = "icehott-ai-workspace"
PROTOCOLS = ("grpc", "http/protobuf")
PROBE_URLS = "health,ready"
REDACTED = "[redacted]"

_SERVICE_NAME = re.compile(r"^[a-z0-9][a-z0-9._-]{0,62}$")
_HEADER_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")
_SENSITIVE = re.compile(
    r"(password|passwd|secret|token|authorization|cookie|api[_-]?key|credential|"
    r"connection[_-]?string|prompt|content|payload|bearer|jwt|private[_-]?key)",
    re.IGNORECASE,
)
_DROPPED = {
    "db.statement",
    "db.query.text",
    "db.user",
    "db.connection_string",
    "url.query",
    "url.full",
    "url.original",
    "http.url",
    "http.target",
    "http.request.body",
    "http.response.body",
    "exception.stacktrace",
    "exception.message",
}


class TelemetryConfigError(ValueError):
    """Raised for invalid telemetry configuration. Messages never contain configured values."""


@dataclass(frozen=True)
class TelemetrySettings:
    enabled: bool = False
    endpoint: str = ""
    protocol: str = "grpc"
    headers: str | None = field(default=None, repr=False)
    timeout_seconds: int = 5
    sampling_ratio: float = 0.1
    traces_enabled: bool = True
    metrics_enabled: bool = True
    logs_enabled: bool = True
    service_name: str = SERVICE_NAME_DEFAULT
    environment: str = "development"
    release_sha: str = "unknown"

    def signal_endpoint(self, path: str) -> str:
        if self.protocol == "http/protobuf":
            return self.endpoint.rstrip("/") + "/" + path
        return self.endpoint


def _flag(env: Mapping[str, str], key: str, default: bool, errors: list[str]) -> bool:
    raw = (env.get(key) or "").strip().lower()
    if not raw:
        return default
    if raw in ("true", "false"):
        return raw == "true"
    errors.append(f"{key}: must be 'true' or 'false'.")
    return default


def load_settings(env: Mapping[str, str] | None = None) -> TelemetrySettings:
    """Parse and validate ``OBSERVABILITY_*`` configuration."""
    env = os.environ if env is None else env
    errors: list[str] = []

    enabled = _flag(env, "OBSERVABILITY_ENABLED", False, errors)
    allow_insecure = _flag(env, "OBSERVABILITY_OTLP_ALLOW_INSECURE", False, errors)
    traces = _flag(env, "OBSERVABILITY_TRACES_ENABLED", True, errors)
    metrics = _flag(env, "OBSERVABILITY_METRICS_ENABLED", True, errors)
    logs = _flag(env, "OBSERVABILITY_LOGS_ENABLED", True, errors)

    tier = (env.get("DEPLOYMENT_TIER") or "").strip().lower()
    hosted = tier in ("staging", "production")
    environment = tier or (env.get("DEPLOYMENT_ENVIRONMENT") or "development").strip().lower()
    raw_sha = (env.get("RELEASE_GIT_SHA") or "").strip().lower()
    sha = raw_sha if re.fullmatch(r"[0-9a-f]{7,64}", raw_sha) else "unknown"

    endpoint = (env.get("OBSERVABILITY_OTLP_ENDPOINT") or "").strip()
    protocol = (env.get("OBSERVABILITY_OTLP_PROTOCOL") or "grpc").strip().lower()
    headers = env.get("OBSERVABILITY_OTLP_HEADERS")
    name = (env.get("OBSERVABILITY_SERVICE_NAME") or SERVICE_NAME_DEFAULT).strip()
    ratio_raw = (env.get("OBSERVABILITY_TRACES_SAMPLING_RATIO") or "").strip()
    timeout_raw = (env.get("OBSERVABILITY_OTLP_TIMEOUT_SECONDS") or "").strip()
    ratio = 0.1
    timeout = 5

    if enabled:
        if not _SERVICE_NAME.match(name):
            errors.append("OBSERVABILITY_SERVICE_NAME: must be 1-63 lowercase letters, digits, '.', '_' or '-'.")
        if not endpoint:
            errors.append("OBSERVABILITY_OTLP_ENDPOINT: is required while observability is enabled.")
        else:
            try:
                parsed = urlparse(endpoint)
                valid = parsed.scheme in ("http", "https") and bool(parsed.hostname)
                _ = parsed.port  # validates the port number
            except ValueError:
                valid = False
                parsed = None
            if not valid or parsed is None:
                errors.append("OBSERVABILITY_OTLP_ENDPOINT: must be an absolute http or https URL.")
            else:
                if parsed.username or parsed.password:
                    errors.append(
                        "OBSERVABILITY_OTLP_ENDPOINT: must not embed credentials; use OBSERVABILITY_OTLP_HEADERS."
                    )
                if parsed.query or parsed.fragment:
                    errors.append("OBSERVABILITY_OTLP_ENDPOINT: must not contain a query string or fragment.")
                if hosted and parsed.scheme != "https" and not allow_insecure:
                    errors.append(
                        "OBSERVABILITY_OTLP_ENDPOINT: must use https unless OBSERVABILITY_OTLP_ALLOW_INSECURE is explicitly true."
                    )
        if protocol not in PROTOCOLS:
            errors.append("OBSERVABILITY_OTLP_PROTOCOL: must be 'grpc' or 'http/protobuf'.")
        if ratio_raw:
            try:
                ratio = float(ratio_raw)
                if math.isnan(ratio) or ratio < 0.0 or ratio > 1.0:
                    raise ValueError
            except ValueError:
                errors.append("OBSERVABILITY_TRACES_SAMPLING_RATIO: must be a number between 0 and 1.")
                ratio = 0.1
        if timeout_raw:
            if timeout_raw.isdigit() and 1 <= int(timeout_raw) <= 30:
                timeout = int(timeout_raw)
            else:
                errors.append("OBSERVABILITY_OTLP_TIMEOUT_SECONDS: must be an integer between 1 and 30.")
        if headers and headers.strip():
            for pair in (p.strip() for p in headers.split(",") if p.strip()):
                key, sep, value = pair.partition("=")
                if not sep or not value or not _HEADER_NAME.match(key.strip()):
                    errors.append("OBSERVABILITY_OTLP_HEADERS: must be a comma-separated list of name=value pairs.")
                    break
        if hosted and not re.fullmatch(r"[0-9a-f]{40}", raw_sha):
            errors.append("RELEASE_GIT_SHA: hosted observability requires the exact 40-character Git commit SHA.")
        if not (traces or metrics or logs):
            errors.append("OBSERVABILITY_ENABLED: at least one of traces, metrics or logs must be enabled.")

    if errors:
        raise TelemetryConfigError(" ".join(errors))

    return TelemetrySettings(
        enabled=enabled,
        endpoint=endpoint,
        protocol=protocol if protocol in PROTOCOLS else "grpc",
        headers=headers if headers and headers.strip() else None,
        timeout_seconds=timeout,
        sampling_ratio=ratio,
        traces_enabled=traces,
        metrics_enabled=metrics,
        logs_enabled=logs,
        service_name=name if _SERVICE_NAME.match(name) else SERVICE_NAME_DEFAULT,
        environment=environment,
        release_sha=sha,
    )


def build_resource(settings: TelemetrySettings) -> Resource:
    """Only safe, low-cardinality resource attributes."""
    version = settings.release_sha if settings.release_sha != "unknown" else "0.0.0"
    return Resource.create(
        {
            "service.name": settings.service_name,
            "service.namespace": "icehott",
            "service.version": version,
            "deployment.environment": settings.environment,
            "deployment.environment.name": settings.environment,
            "icehott.service.role": "Ai",
            "icehott.application": APPLICATION_NAME,
            "icehott.release.git_sha": settings.release_sha,
        }
    )


def sanitize_attributes(attributes: Mapping[str, object] | None) -> dict[str, object]:
    """Drop payload-bearing attributes and mask sensitive-named ones."""
    clean: dict[str, object] = {}
    for key, value in (attributes or {}).items():
        lowered = key.lower()
        if (
            lowered in _DROPPED
            or lowered.startswith("http.request.header.")
            or lowered.startswith("http.response.header.")
            or lowered.startswith("db.query.parameter")
        ):
            continue
        clean[key] = REDACTED if _SENSITIVE.search(key) else value
    return clean


class SanitizingSpanExporter(SpanExporter):
    """Wraps an exporter and exports sanitized copies of every span."""

    def __init__(self, inner: SpanExporter) -> None:
        self._inner = inner

    def export(self, spans: Sequence[ReadableSpan]) -> SpanExportResult:
        return self._inner.export([self._sanitize(span) for span in spans])

    @staticmethod
    def _sanitize(span: ReadableSpan) -> ReadableSpan:
        events = tuple(
            type(event)(
                name=event.name,
                attributes=sanitize_attributes(event.attributes),
                timestamp=event.timestamp,
            )
            for event in span.events
        )
        return ReadableSpan(
            name=span.name,
            context=span.context,
            parent=span.parent,
            resource=span.resource,
            attributes=sanitize_attributes(span.attributes),
            events=events,
            links=span.links,
            kind=span.kind,
            status=span.status,
            start_time=span.start_time,
            end_time=span.end_time,
            instrumentation_scope=span.instrumentation_scope,
        )

    def shutdown(self) -> None:
        self._inner.shutdown()

    def force_flush(self, timeout_millis: int = 30000) -> bool:
        return self._inner.force_flush(timeout_millis)


class _SafeTelemetryLogFilter(logging.Filter):
    """Handler-local log sanitization.

    Python 3.12 filters may return a replacement LogRecord. We use that to keep
    console logging untouched while the OTLP handler receives only a generic body
    plus standard logger metadata and current trace/span correlation. Arbitrary
    application messages and custom extras are never exported.
    """

    _allowed_custom = {"otelTraceID", "otelSpanID", "otelTraceSampled", "otelServiceName"}

    def filter(self, record: logging.LogRecord):
        if record.name.startswith("opentelemetry"):
            return False

        clone = logging.makeLogRecord(record.__dict__.copy())
        clone.msg = "application_log"
        clone.args = ()
        clone.exc_info = None
        clone.exc_text = None
        clone.stack_info = None

        standard = logging.LogRecord(
            name="",
            level=logging.INFO,
            pathname="",
            lineno=0,
            msg="",
            args=(),
            exc_info=None,
        ).__dict__.keys()
        for key in list(clone.__dict__):
            if key not in standard and key not in self._allowed_custom:
                del clone.__dict__[key]

        return clone


class TelemetryHandle:
    """Owns the providers created for this process so they can be shut down cleanly."""

    def __init__(self) -> None:
        self.tracer_provider: TracerProvider | None = None
        self.meter_provider = None
        self.logger_provider = None
        self._uninstrument: list = []

    def shutdown(self) -> None:
        for undo in reversed(self._uninstrument):
            try:
                undo()
            except Exception:  # pragma: no cover - best effort
                pass
        for provider in (self.tracer_provider, self.meter_provider, self.logger_provider):
            if provider is not None:
                try:
                    provider.shutdown()
                except Exception:  # pragma: no cover - telemetry must never raise
                    pass


def _span_exporter(settings: TelemetrySettings) -> SpanExporter:
    if settings.protocol == "http/protobuf":
        from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
    else:
        from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
    return OTLPSpanExporter(
        endpoint=settings.signal_endpoint("v1/traces"),
        headers=_headers(settings),
        timeout=settings.timeout_seconds,
    )


def _headers(settings: TelemetrySettings) -> dict[str, str] | None:
    if not settings.headers:
        return None
    pairs = (p.strip().partition("=") for p in settings.headers.split(",") if p.strip())
    return {k.strip(): v.strip() for k, _, v in pairs}


def configure_telemetry(
    app,
    settings: TelemetrySettings | None = None,
    *,
    span_exporter: SpanExporter | None = None,
) -> TelemetryHandle | None:
    """Instrument ``app``. Returns ``None`` when telemetry is disabled."""
    settings = settings or load_settings()
    if not settings.enabled:
        return None

    from opentelemetry import metrics as metrics_api
    from opentelemetry import trace as trace_api
    from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor
    from opentelemetry.instrumentation.httpx import HTTPXClientInstrumentor
    from opentelemetry.instrumentation.logging import LoggingInstrumentor

    # Stable HTTP semantic conventions: route-based, low-cardinality attributes (no http.host).
    os.environ.setdefault("OTEL_SEMCONV_STABILITY_OPT_IN", "http")

    handle = TelemetryHandle()
    resource = build_resource(settings)

    if settings.traces_enabled:
        provider = TracerProvider(
            resource=resource,
            sampler=ParentBased(TraceIdRatioBased(settings.sampling_ratio)),
        )
        exporter = span_exporter if span_exporter is not None else _span_exporter(settings)
        provider.add_span_processor(BatchSpanProcessor(SanitizingSpanExporter(exporter)))
        handle.tracer_provider = provider
        trace_api.set_tracer_provider(provider)

    if settings.metrics_enabled:
        from opentelemetry.instrumentation.system_metrics import SystemMetricsInstrumentor
        from opentelemetry.sdk.metrics import MeterProvider
        from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader

        if settings.protocol == "http/protobuf":
            from opentelemetry.exporter.otlp.proto.http.metric_exporter import OTLPMetricExporter
        else:
            from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter
        reader = PeriodicExportingMetricReader(
            OTLPMetricExporter(
                endpoint=settings.signal_endpoint("v1/metrics"),
                headers=_headers(settings),
                timeout=settings.timeout_seconds,
            ),
            export_interval_millis=30_000,
            export_timeout_millis=settings.timeout_seconds * 1000,
        )
        meter_provider = MeterProvider(resource=resource, metric_readers=[reader])
        handle.meter_provider = meter_provider
        metrics_api.set_meter_provider(meter_provider)
        SystemMetricsInstrumentor(
            config={
                "process.cpu.time": ["user", "system"],
                "process.memory.usage": None,
                "process.cpu.utilization": None,
                "process.thread.count": None,
                "process.runtime.gc_count": None,
            }
        ).instrument(meter_provider=meter_provider)
        handle._uninstrument.append(SystemMetricsInstrumentor().uninstrument)

    if settings.logs_enabled:
        from opentelemetry._logs import set_logger_provider
        from opentelemetry.instrumentation.logging.handler import LoggingHandler
        from opentelemetry.sdk._logs import LoggerProvider
        from opentelemetry.sdk._logs.export import BatchLogRecordProcessor

        if settings.protocol == "http/protobuf":
            from opentelemetry.exporter.otlp.proto.http._log_exporter import OTLPLogExporter
        else:
            from opentelemetry.exporter.otlp.proto.grpc._log_exporter import OTLPLogExporter
        logger_provider = LoggerProvider(resource=resource)
        logger_provider.add_log_record_processor(
            BatchLogRecordProcessor(
                OTLPLogExporter(
                    endpoint=settings.signal_endpoint("v1/logs"),
                    headers=_headers(settings),
                    timeout=settings.timeout_seconds,
                )
            )
        )
        set_logger_provider(logger_provider)
        log_handler = LoggingHandler(level=logging.INFO, logger_provider=logger_provider)
        log_handler.addFilter(_SafeTelemetryLogFilter())
        logging.getLogger().addHandler(log_handler)
        handle.logger_provider = logger_provider
        handle._uninstrument.append(lambda: logging.getLogger().removeHandler(log_handler))

    # Adds otelTraceID/otelSpanID to every log record for correlation with traces.
    LoggingInstrumentor().instrument(
        set_logging_format=True,
        logging_format=(
            "%(asctime)s %(levelname)s [trace_id=%(otelTraceID)s span_id=%(otelSpanID)s] "
            "%(name)s: %(message)s"
        ),
    )
    handle._uninstrument.append(LoggingInstrumentor().uninstrument)

    # W3C traceparent extraction is the FastAPI/ASGI instrumentation default; incoming context is
    # continued. Probes are excluded. Headers and bodies are not captured.
    FastAPIInstrumentor.instrument_app(
        app,
        tracer_provider=handle.tracer_provider,
        meter_provider=handle.meter_provider,
        excluded_urls=PROBE_URLS,
    )
    handle._uninstrument.append(lambda: FastAPIInstrumentor.uninstrument_app(app))
    HTTPXClientInstrumentor().instrument(tracer_provider=handle.tracer_provider)
    handle._uninstrument.append(HTTPXClientInstrumentor().uninstrument)

    return handle
