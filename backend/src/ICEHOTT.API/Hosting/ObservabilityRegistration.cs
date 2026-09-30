using ICEHOTT.Application.Knowledge;
using ICEHOTT.Application.Observability;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ICEHOTT.API.Hosting;

public static class ObservabilityRegistration
{
    public const string ApplicationName = "icehott-ai-workspace";
    private static readonly string[] ProbePaths = ["/health", "/ready", "/release"];

    /// <summary>
    /// Log/trace correlation is always on (cheap and local). Exporting is opt-in via
    /// <c>Observability:Enabled</c>. Telemetry is auxiliary: exporters are asynchronous and bounded,
    /// nothing here is wired into health or readiness, and a dead collector never fails a request.
    /// </summary>
    public static void AddIcehottObservability(
        this WebApplicationBuilder builder,
        ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(release);

        // Adds TraceId/SpanId to the logging scope so console log lines correlate with traces.
        builder.Logging.Configure(options =>
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        builder.Logging.AddSimpleConsole(console => console.IncludeScopes = true);

        var options = ObservabilityOptions.Load(builder.Configuration);
        if (!options.Enabled)
            return;

        var resource = CreateResource(options, release);
        var otel = builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService(
            serviceName: resource.Name,
            serviceNamespace: "icehott",
            serviceVersion: resource.Version)
            .AddAttributes(resource.Attributes));

        if (options.TracesEnabled)
        {
            otel.WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.SamplingRatio)))
                .AddAspNetCoreInstrumentation(aspNet =>
                {
                    aspNet.RecordException = false;
                    aspNet.Filter = context => !IsProbe(context.Request.Path.Value);
                })
                .AddHttpClientInstrumentation(http => http.RecordException = false)
                .AddNpgsql()
                .AddSource(IcehottMetrics.MeterName, RagTelemetry.SourceName)
                .AddProcessor(new SensitiveTagRedactionProcessor())
                .AddOtlpExporter(exporter => ConfigureExporter(exporter, options, "v1/traces")));
        }

        if (options.MetricsEnabled)
        {
            otel.WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(IcehottMetrics.MeterName, RagTelemetry.SourceName)
                .AddOtlpExporter(exporter => ConfigureExporter(exporter, options, "v1/metrics")));
        }

        if (options.LogsEnabled)
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(
                    serviceName: resource.Name,
                    serviceNamespace: "icehott",
                    serviceVersion: resource.Version).AddAttributes(resource.Attributes));
                // The message template is exported, never the rendered text or scopes.
                logging.IncludeFormattedMessage = false;
                logging.IncludeScopes = false;
                logging.ParseStateValues = true;
                logging.AddProcessor(new SensitiveLogRedactionProcessor());
                logging.AddOtlpExporter(exporter => ConfigureExporter(exporter, options, "v1/logs"));
            });
        }
    }

    public sealed record ResourceIdentity(
        string Name,
        string Version,
        IReadOnlyList<KeyValuePair<string, object>> Attributes);

    /// <summary>Only safe, low-cardinality resource attributes.</summary>
    public static ResourceIdentity CreateResource(ObservabilityOptions options, ReleaseInfo release)
    {
        var name = string.IsNullOrWhiteSpace(options.ServiceName) ? release.Service : options.ServiceName!;
        var version = release.GitSha != ReleaseInfo.UnknownSha ? release.GitSha : release.Version;

        return new ResourceIdentity(
            name,
            version,
            [
                new("deployment.environment.name", release.Environment),
                new("deployment.environment", release.Environment),
                new("icehott.service.role", release.Role),
                new("icehott.application", ApplicationName),
                new("icehott.release.git_sha", release.GitSha)
            ]);
    }

    public static bool IsProbe(string? path) =>
        path is not null && ProbePaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));

    private static void ConfigureExporter(
        OtlpExporterOptions exporter,
        ObservabilityOptions options,
        string signalPath)
    {
        exporter.Endpoint = options.SignalEndpoint(signalPath);
        exporter.Protocol = options.Protocol == ObservabilityOptions.ProtocolHttp
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
        exporter.TimeoutMilliseconds = options.TimeoutSeconds * 1000;
        if (!string.IsNullOrWhiteSpace(options.Headers))
            exporter.Headers = options.Headers;
    }
}
