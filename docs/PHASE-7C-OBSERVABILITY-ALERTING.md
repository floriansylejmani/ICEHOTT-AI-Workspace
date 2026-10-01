# Phase 7C — Observability & Alerting

**Status:** Implemented on `phase-7c-observability-alerting`, pending review

**Baseline:** `main` at `6c0fc987d3776bc402685cf10f01fc01e4f8caf2`

**Parent architecture:** [PHASE-7-PRODUCTION-DEPLOYMENT-SRE.md](PHASE-7-PRODUCTION-DEPLOYMENT-SRE.md) (builds on [7A](PHASE-7A-PRODUCTION-ENVIRONMENT-RELEASE-FOUNDATION.md) and [7B](PHASE-7B-MANAGED-DATA-OBJECT-STORAGE.md))

Phase 7C adds OpenTelemetry traces, metrics and logs to the ASP.NET API/Worker and the FastAPI AI service, W3C trace correlation across them, a local/staging OpenTelemetry Collector, and provider-neutral dashboards and alert rules. It does not change the durable authority model (PostgreSQL) and adds no database migration and no Redis dependency.

Verification legend used below: **[local]** proven on the developer machine, **[CI]** enforced by the CI workflow, **[live]** requires verification against a real production provider.

## 1. Architecture

```text
client --traceparent--> ASP.NET API ----traceparent----> FastAPI AI service
                            |                                  |
   ASP.NET Worker (jobs)    | OTLP (grpc | http/protobuf)      | OTLP
            |               v                                  v
            +-------> OpenTelemetry Collector <----------------+
                        |  debug exporter (local)
                        |  Prometheus exporter :8889  -> Prometheus / any OpenMetrics scraper
                        +-> optional OTLP/HTTP upstream (overlay, credentials from secrets)
```

* Telemetry is **auxiliary**. It is off by default, exports asynchronously with bounded queues and short timeouts, and is never part of `/health`, `/ready` or any request path. A dead collector cannot fail or slow a request.
* The .NET SDK setup lives in `Hosting/ObservabilityRegistration.cs`; validation in `Hosting/ObservabilityOptions.cs` (reused by `ProductionConfigurationValidator`); redaction in `Hosting/TelemetryRedaction.cs`; shared custom metrics in `ICEHOTT.Application/Observability/IcehottMetrics.cs`. The AI service uses `app/telemetry.py`.
* Resource attributes (all low cardinality): `service.name` (`icehott-api`, `icehott-worker`, `icehott-ai`, overridable), `service.namespace=icehott`, `service.version` (full release Git SHA, else assembly version), `deployment.environment` and `deployment.environment.name` (deployment tier, else ASP.NET environment), `icehott.service.role` (`Api`, `Worker`, `Ai`), `icehott.application=icehott-ai-workspace`, `icehott.release.git_sha`.

## 2. Configuration keys

### ASP.NET (API and Worker)

| Key | Default | Notes |
| --- | --- | --- |
| `Observability:Enabled` | `false` | Master switch. Nothing is exported unless `true` |
| `Observability:Required` | `false` | If `true`, `Enabled` must also be `true` (prevents silently running without telemetry) |
| `Observability:ServiceName` | role based | Optional override, `^[a-z0-9][a-z0-9._-]{0,62}$` |
| `Observability:Otlp:Endpoint` | none | Required when enabled. Absolute http/https, no embedded credentials, no query or fragment |
| `Observability:Otlp:Protocol` | `grpc` | `grpc` or `http/protobuf` (per-signal `/v1/{traces,metrics,logs}` paths are appended for HTTP) |
| `Observability:Otlp:AllowInsecureTransport` | `false` | Hosted tiers require `https` unless this is explicitly `true` (for example a private-network collector) |
| `Observability:Otlp:Headers` | none | Secret. `name=value,name2=value2`. Environment/secret store only |
| `Observability:Otlp:TimeoutSeconds` | `5` | 1–30 |
| `Observability:Traces:Enabled` / `Metrics:Enabled` / `Logs:Enabled` | `true` | At least one must be on when enabled |
| `Observability:Traces:SamplingRatio` | `0.1` | 0–1, parent-based ratio sampling |

Environment variable form: `Observability__Enabled`, `Observability__Otlp__Endpoint`, and so on.

Hosted tiers (`staging`/`production`): `ProductionConfigurationValidator` runs the same validation fail-closed, so malformed telemetry configuration stops startup with messages that name **keys only** (never endpoint credentials or header values). Outside hosted tiers it is validated only when `Observability:Enabled=true`, so local development is unaffected. `appsettings.json` ships with export disabled and no endpoint or credential.

### FastAPI AI service (environment variables)

`OBSERVABILITY_ENABLED` (default `false`), `OBSERVABILITY_OTLP_ENDPOINT`, `OBSERVABILITY_OTLP_PROTOCOL` (`grpc`|`http/protobuf`), `OBSERVABILITY_OTLP_HEADERS` (secret), `OBSERVABILITY_OTLP_TIMEOUT_SECONDS`, `OBSERVABILITY_OTLP_ALLOW_INSECURE`, `OBSERVABILITY_TRACES_ENABLED`, `OBSERVABILITY_METRICS_ENABLED`, `OBSERVABILITY_LOGS_ENABLED`, `OBSERVABILITY_TRACES_SAMPLING_RATIO`, `OBSERVABILITY_SERVICE_NAME`, plus `DEPLOYMENT_TIER`, `DEPLOYMENT_ENVIRONMENT` and `RELEASE_GIT_SHA` (the AI image accepts `--build-arg GIT_SHA`). When observability is enabled in `staging` or `production`, `RELEASE_GIT_SHA` must be the exact 40-character commit SHA. Same key-only error messages apply; invalid configuration fails startup when enabled.

## 3. Local verification

Run the stack with the overlay (adds only the collector; the base compose file keeps its architecture, host ports are now overridable):

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml up --build
```

Or run the scripted proof, which uses isolated ports and tears everything down:

```bash
bash scripts/observability/verify-trace.sh
```

It (1) starts PostgreSQL, Redis, the AI service, the API and the collector; (2) registers a user and sends a chat request carrying a chosen `traceparent`; (3) requires the collector to have received spans for **that trace id** from both `icehott-api` and `icehott-ai`; (4) requires ASP.NET Core and custom ICEHOTT metrics on the collector's Prometheus endpoint with no identifier-shaped labels, and log records at the collector; (5) stops the collector, sends more chat requests and requires HTTP 200, healthy `/health` on both services and both containers still running.

## 4. Production setup

1. Provide a collector reachable from the API, Worker and AI services (private network preferred). Use `deploy/otel/collector.yaml`, optionally with `deploy/otel/collector.upstream.yaml` as a second `--config` to forward to a hosted OTLP/HTTP backend.
2. Inject secrets from the platform secret store only: `OTEL_UPSTREAM_ENDPOINT`, `OTEL_UPSTREAM_HEADERS` (collector) and `Observability__Otlp__Headers` / `OBSERVABILITY_OTLP_HEADERS` (services, if the collector requires auth). Nothing provider specific or secret is committed.
3. Set on API, Worker and AI: `Observability__Enabled=true` (`OBSERVABILITY_ENABLED=true`), the endpoint, `Observability__Otlp__Protocol`, and a calibrated `SamplingRatio`. Use `https` or, for a private-network collector, `Observability__Otlp__AllowInsecureTransport=true` deliberately. Set `Observability__Required=true` on services where missing telemetry must block deployment.
4. Load `deploy/otel/alerts/icehott-rules.yml` into Prometheus (or a compatible ruler), scrape the collector on `:8889` (metrics) and `:8888` (collector self metrics, `job="otel-collector"`), and add a black-box probe of `GET /ready` named `icehott-api-ready`.
5. Route `critical` to the on-call channel and `warning` to a ticket queue. Destination credentials are an operational task (not in this repository).

## 5. Collector behavior

`deploy/otel/collector.yaml`: OTLP receivers on gRPC `4317` and HTTP `4318`; processors `memory_limiter` (256 MiB), `attributes/redact` (drops authorization/cookie headers, SQL text and query strings as defense in depth) and `batch`; exporters `debug` (verbosity from `OTEL_DEBUG_VERBOSITY`, default `basic`; `detailed` in the compose overlay so trace ids are printable) and `prometheus`; `health_check` on `13133`; traces, metrics and logs pipelines. The upstream overlay adds a bounded sending queue and bounded retries (`max_elapsed_time: 120s`), so a slow backend causes dropped telemetry instead of backpressure. The image is pinned by digest (`otel/opentelemetry-collector-contrib@sha256:4539…c621`, release 0.136.0).

## 6. Custom metrics

Meter `ICEHOTT` (all tag values come from closed sets; no identifiers, names, paths, keys or messages):

| Metric (OTel name → Prometheus) | Type | Tags | Meaning |
| --- | --- | --- | --- |
| `icehott.artifact.store.operations` → `icehott_artifact_store_operations_total` | counter | `provider` (`local`,`s3`), `operation` (`stage`,`commit`,`info`,`read`,`delete`,`delete_staging`,`cleanup_staging`), `outcome` (`success`,`unavailable`,`integrity`,`too_large`,`not_found`,`cancelled`,`error`) | Artifact store calls (decorator `InstrumentedArtifactStore`) |
| `icehott.artifact.store.unavailable` → `…_total` | counter | `provider`, `operation` | Provider-unavailable failures |
| `icehott.ai_runtime.requests` → `…_total` | counter | `operation` (`chat`,`embeddings`), `outcome` (`success`,`failure`,`cancelled`) | API → AI runtime requests |
| `icehott.ai_runtime.request.duration` → `…_seconds` | histogram | `operation`, `outcome` | Duration of those requests |
| `icehott.readiness.checks` → `…_total` | counter | `component` (`database`,`ai_runtime`), `outcome` (`ok`,`failed`) | Readiness dependency checks (drives the PostgreSQL alert) |
| `icehott.workflow.runs.processed` → `…_total` | counter | `outcome` (`waiting`,`completed`,`lease_lost`) | Workflow run leases processed |
| `icehott.workflow.runner.loop_failures` → `…_total` | counter | none | Unexpected runner loop failures |
| `icehott.workflow.scheduler.actions` → `…_total` | counter | `disposition` (`claimed`,`runcreated`,`skipped`,`failed`) | Scheduler actions |
| `icehott.workflow.scheduler.loop_failures` → `…_total` | counter | none | Unexpected scheduler loop failures |

The pre-existing `ICEHOTT.Rag` meter (`rag.jobs.completed`, `rag.jobs.retried`, `rag.indexing.failures`, retrieval counters and histograms) is exported as well and covers knowledge worker outcomes. Tool execution counters are intentionally not added in this phase. Also exported: ASP.NET Core server metrics, HttpClient metrics and stable .NET runtime metrics; on the AI service FastAPI server metrics and process metrics. The prerelease .NET `OpenTelemetry.Instrumentation.Process` package is intentionally not used. The Npgsql meter is **not** exported because its pool-name tag embeds connection details; PostgreSQL is covered by traces and the readiness metric.

## 7. Trace propagation

The API's `HttpClient` instrumentation injects the standard W3C `traceparent` (and `tracestate`) header on every call to the AI service; the FastAPI/ASGI instrumentation extracts it and continues the same trace. No custom, secret-bearing or bespoke correlation header is used. Health, readiness and release probes are excluded from tracing. Sampling is parent-based, so a sampled API request is always sampled at the AI service too.

* [CI] `TracePropagationTests` sends a real `AiRuntimeClient` request to a local server and asserts the received `traceparent` carries the caller's trace id and that no `x-icehott-*` or `authorization` header was added; `test_telemetry.py` asserts the FastAPI server span continues an incoming `traceparent` (same trace id, parent span id).
* [CI] `verify-trace.sh` proves API → AI → Collector with one trace id end to end.

## 8. Logging correlation

* .NET: `ActivityTrackingOptions` (`TraceId`, `SpanId`) plus console scopes put `TraceId`/`SpanId` on every console line; OTLP log records carry trace/span ids natively. The logger configuration is unchanged otherwise; no verbose framework or EF logging is enabled.
* Python: `LoggingInstrumentor` injects `otelTraceID`/`otelSpanID` and the console log format prints them. The OTLP log handler applies a handler-local Python 3.12 filter that exports only the generic body `application_log` plus standard logger metadata/trace correlation; arbitrary application message text, custom extras, exception text and stack traces are not exported. Console records remain unchanged.
* Exported .NET logs send the message **template** (not the rendered text), no scopes, no exception message or stack trace (only `exception.type`), and attribute names matching the sensitive-name policy are replaced by `[redacted]`. The OTel SDK's own log category is excluded from the Python log exporter to prevent an export-failure feedback loop.

## 9. Redaction policy

Never exported: `Authorization` headers, cookies, JWTs, database connection strings, object-storage keys, provider API keys, prompts, model responses, artifact content or bytes, tool arguments/results, workflow audit payloads, request/response bodies, SQL text or parameters, query strings, exception messages and stack traces.

Enforcement (defense in depth): instrumentations are configured without body/header capture and without `RecordException`; span processors/export sanitizers drop `db.statement`, `db.query.text`, `url.query`, `url.full`, `url.original`, `http.url`, `http.target`, header/body tags and mask any tag whose name looks sensitive; .NET log processing strips exceptions and masks sensitive attribute names; the Python OTLP log handler replaces arbitrary application message bodies and custom extras before export; the collector deletes the same sensitive attributes again. Tests: `TelemetryRedactionTests`, `ObservabilityCommittedConfigTests`, `test_telemetry.py`. Validation messages never contain configured values.

## 10. Cardinality policy

Resource attributes are the only place service identity lives. Metric tags are restricted to closed enumerations (see the table); no user, workspace, artifact, run, conversation or trigger identifier, no free text and no URL is ever a metric label. HTTP server metrics use route templates. `OperationalMetricsTests` asserts the exact tag-name set and that keys/messages never appear as tag values; `verify-trace.sh` greps the exported series for identifier-shaped labels. Trace sampling defaults to 10 %.

## 11. Failure behavior

* Exporters are asynchronous (batch processors / periodic readers) with a bounded queue and a 1–30 s timeout; a failing export drops data and logs at the SDK's own diagnostic level; there is no application-level retry loop.
* Collector or backend down: requests, readiness and workers are unaffected. Proven by `TelemetryOutageTests` (dead loopback endpoint, five requests), `test_unreachable_collector_does_not_break_requests_or_shutdown`, and the collector-stop step of `verify-trace.sh`.
* Explicitly required telemetry (`Observability:Required=true`) fails startup validation if not enabled or malformed; otherwise telemetry stays optional.

## 12. Dashboards

Provider-neutral panel definitions (build in Grafana or any equivalent):

* **API health**: request rate, `icehott:http_server_5xx_ratio:rate5m`, p95/p99 `http_server_request_duration_seconds`, readiness `probe_success`.
* **Dependencies**: `icehott:readiness_failure_ratio:rate5m` by component; `icehott_ai_runtime_requests_total` by outcome and `icehott_ai_runtime_request_duration_seconds` p95.
* **Artifacts/storage**: `icehott_artifact_store_operations_total` by provider/operation/outcome; `icehott_artifact_store_unavailable_total`.
* **Workers**: `icehott_workflow_runs_processed_total` by outcome, scheduler actions by disposition, loop-failure counters, `rag_jobs_*`.
* **Runtime**: stable .NET runtime metrics and AI process metrics.
* **Telemetry pipeline**: collector `up`, `otelcol_exporter_send_failed_*`, `otelcol_processor_refused_*`.

## 13. Alert rules

`deploy/otel/alerts/icehott-rules.yml` (Prometheus format, validated and unit-tested with `promtool` in CI). **All thresholds are initial baselines that must be calibrated against real traffic before paging.** Rules use `for:` windows and minimum-traffic guards to avoid alert fatigue.

| Alert | Severity | Baseline condition |
| --- | --- | --- |
| `IcehottApiNotReady` | critical | `/ready` probe failing 2 min |
| `IcehottHigh5xxRate` | critical | > 2 % 5xx for 5 min and > 0.2 req/s |
| `IcehottHighApiLatency` | warning | p95 > 2 s for 10 min |
| `IcehottDatabaseUnreachable` | critical | > 50 % database readiness checks failing for 3 min |
| `IcehottArtifactStorageUnavailable` | critical | > 5 unavailable errors in 10 min, sustained 5 min |
| `IcehottAiRuntimeFailing` / `IcehottAiReadinessFailing` | warning | > 25 % request failures / > 50 % readiness failures for 5 min |
| `IcehottWorkflowWorkerFailing` | critical | > 3 runner or scheduler loop failures in 10 min |
| `IcehottWorkflowLeasesLost` | warning | > 5 lost leases in 15 min |
| `IcehottTelemetryPipelineDown` / `IcehottTelemetryExportFailing` / `IcehottApiMetricsMissing` | warning | collector down 10 min / export failures 15 min / no API metrics 20 min |

**Migration failure** is a deployment-pipeline event, not an application metric: the migrator is a one-shot job. Alert on failure of the migration step in `deploy-staging.yml` / `promote-production.yml` through the CI/CD notification channel (a failed migration already blocks promotion). Load-test-derived and SLO-burn-rate alerting belongs to Phase 7E.

## 14. Verification status

**Proven locally:** .NET and Python unit/integration tests; collector configuration validation; `promtool` rule check and unit tests; end-to-end API → AI → Collector trace with a fixed trace id, metrics and logs at the collector, collector-outage tolerance (`scripts/observability/verify-trace.sh`).

**Proven in CI:** everything above via the `backend`, `ai` and new `observability` jobs (collector config validation with the pinned image, rule check/tests, compose validation, and the end-to-end script). No external SaaS account is used.

**Requires live production provider verification:** ingestion into the chosen hosted backend (auth headers, TLS, quotas, retention, sampling cost); private-network collector reachability and `AllowInsecureTransport` usage on the hosting platform; the `/ready` black-box probe; alert routing/paging destinations and threshold calibration; and that hosted backends honor the redaction expectations. The prerelease .NET process-instrumentation package is deliberately excluded; Phase 7C uses stable .NET runtime instrumentation.

## 15. Deferred (out of scope)

Backup/restore and DR drills (7D); k6/load/soak and the final production release gate (7E); production rollout and live alert-destination credentials; Kubernetes; SLO burn-rate alerts; tool-execution metrics.
