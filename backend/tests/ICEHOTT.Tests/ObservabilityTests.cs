using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using ICEHOTT.API.Hosting;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Observability;
using ICEHOTT.Infrastructure.Ai;
using ICEHOTT.Infrastructure.Artifacts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace ICEHOTT.Tests;

public sealed class ObservabilityOptionsTests
{
    private const string EndpointSecret = "endpoint-cred-sentinel-91";
    private const string HeaderSecret = "header-secret-sentinel-58";

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values
                .GroupBy(x => x.Key)
                .ToDictionary(g => g.Key, g => g.Last().Value))
            .Build();

    private static (string, string?)[] Enabled(params (string, string?)[] extra) =>
        new (string, string?)[]
        {
            ("Observability:Enabled", "true"),
            ("Observability:Otlp:Endpoint", "https://collector.example:4317")
        }.Concat(extra).ToArray();

    [Fact]
    public void Disabled_By_Default_And_Requires_Nothing()
    {
        Assert.Empty(ObservabilityOptions.Validate(Config(), hosted: true));
        Assert.False(ObservabilityOptions.Load(Config()).Enabled);
    }

    [Fact]
    public void Enabled_With_Https_Endpoint_Is_Valid_And_Defaults_Are_Safe()
    {
        var config = Config(Enabled());
        Assert.Empty(ObservabilityOptions.Validate(config, hosted: true));

        var options = ObservabilityOptions.Load(config);
        Assert.Equal("grpc", options.Protocol);
        Assert.Equal(0.1, options.SamplingRatio);
        Assert.True(options.TracesEnabled && options.MetricsEnabled && options.LogsEnabled);
        Assert.Equal(5, options.TimeoutSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://collector.example")]
    [InlineData("https://collector.example/?token=x")]
    public void Enabled_Rejects_Missing_Or_Malformed_Endpoint(string endpoint)
    {
        var errors = ObservabilityOptions.Validate(
            Config(("Observability:Enabled", "true"), ("Observability:Otlp:Endpoint", endpoint)),
            hosted: false);

        Assert.Contains(errors, e => e.StartsWith("Observability:Otlp:Endpoint"));
    }

    [Fact]
    public void Hosted_Requires_Https_Unless_Explicitly_Allowed()
    {
        var http = Enabled(("Observability:Otlp:Endpoint", "http://collector.internal:4318"));

        Assert.Contains(
            ObservabilityOptions.Validate(Config(http), hosted: true),
            e => e.StartsWith("Observability:Otlp:Endpoint"));
        Assert.Empty(ObservabilityOptions.Validate(Config(http), hosted: false));
        Assert.Empty(ObservabilityOptions.Validate(
            Config(http.Append(("Observability:Otlp:AllowInsecureTransport", "true")).ToArray()),
            hosted: true));
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    [InlineData("NaN")]
    [InlineData("abc")]
    public void Sampling_Ratio_Must_Be_Between_Zero_And_One(string ratio)
    {
        Assert.Contains(
            ObservabilityOptions.Validate(
                Config(Enabled(("Observability:Traces:SamplingRatio", ratio))), hosted: true),
            e => e.StartsWith("Observability:Traces:SamplingRatio"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.25")]
    [InlineData("1")]
    public void Sampling_Ratio_Accepts_Bounds(string ratio) =>
        Assert.Empty(ObservabilityOptions.Validate(
            Config(Enabled(("Observability:Traces:SamplingRatio", ratio))), hosted: true));

    [Theory]
    [InlineData("Observability:Otlp:Protocol", "carrier-pigeon")]
    [InlineData("Observability:Otlp:TimeoutSeconds", "0")]
    [InlineData("Observability:Otlp:TimeoutSeconds", "999")]
    [InlineData("Observability:Otlp:Headers", "no-equals-sign")]
    [InlineData("Observability:ServiceName", "Bad Name!")]
    [InlineData("Observability:Metrics:Enabled", "maybe")]
    public void Malformed_Settings_Are_Rejected_By_Key(string key, string value)
    {
        Assert.Contains(
            ObservabilityOptions.Validate(Config(Enabled((key, value))), hosted: true),
            e => e.StartsWith(key));
    }

    [Fact]
    public void Required_Without_Enabled_Fails()
    {
        Assert.Contains(
            ObservabilityOptions.Validate(Config(("Observability:Required", "true")), hosted: true),
            e => e.StartsWith("Observability:Enabled"));
    }

    [Fact]
    public void At_Least_One_Signal_Must_Be_Enabled()
    {
        var errors = ObservabilityOptions.Validate(
            Config(Enabled(
                ("Observability:Traces:Enabled", "false"),
                ("Observability:Metrics:Enabled", "false"),
                ("Observability:Logs:Enabled", "false"))),
            hosted: true);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validation_Errors_Never_Contain_Configured_Values()
    {
        var errors = ObservabilityOptions.Validate(
            Config(
                ("Observability:Enabled", "true"),
                ("Observability:Otlp:Endpoint", $"http://user:{EndpointSecret}@collector.example/?k={EndpointSecret}"),
                ("Observability:Otlp:Headers", $"authorization {HeaderSecret}"),
                ("Observability:Traces:SamplingRatio", HeaderSecret)),
            hosted: true);

        Assert.NotEmpty(errors);
        foreach (var error in errors)
        {
            Assert.DoesNotContain(EndpointSecret, error);
            Assert.DoesNotContain(HeaderSecret, error);
            Assert.DoesNotContain("collector.example", error);
        }
    }

    [Fact]
    public void Http_Protocol_Uses_Per_Signal_Paths_And_Grpc_Uses_The_Base_Endpoint()
    {
        var http = ObservabilityOptions.Load(Config(Enabled(
            ("Observability:Otlp:Protocol", "http/protobuf"),
            ("Observability:Otlp:Endpoint", "https://collector.example:4318"))));
        var grpc = ObservabilityOptions.Load(Config(Enabled()));

        Assert.Equal("https://collector.example:4318/v1/traces", http.SignalEndpoint("v1/traces").ToString());
        Assert.Equal("https://collector.example:4317/", grpc.SignalEndpoint("v1/traces").ToString());
    }

    [Theory]
    [InlineData(ServiceRole.Api, "icehott-api", "Api")]
    [InlineData(ServiceRole.Worker, "icehott-worker", "Worker")]
    public void Resource_Carries_Only_Safe_Low_Cardinality_Attributes(
        ServiceRole role, string service, string roleName)
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var release = ReleaseInfo.Create(
            Config(("Deployment:Tier", "staging"), ("Release:GitSha", sha)), "Production", role);

        var resource = ObservabilityRegistration.CreateResource(
            ObservabilityOptions.Load(Config(Enabled())), release);

        Assert.Equal(service, resource.Name);
        Assert.Equal(sha, resource.Version);
        var attributes = resource.Attributes.ToDictionary(x => x.Key, x => x.Value?.ToString());
        Assert.Equal("staging", attributes["deployment.environment.name"]);
        Assert.Equal(roleName, attributes["icehott.service.role"]);
        Assert.Equal("icehott-ai-workspace", attributes["icehott.application"]);
        Assert.DoesNotContain(attributes.Keys, k => k.Contains("user") || k.Contains("workspace.id") || k.Contains("run"));
    }

    [Fact]
    public void Service_Name_Override_Is_Honoured()
    {
        var release = ReleaseInfo.Create(Config(), "Development", ServiceRole.Api);
        var resource = ObservabilityRegistration.CreateResource(
            ObservabilityOptions.Load(Config(Enabled(("Observability:ServiceName", "icehott-api-eu")))), release);

        Assert.Equal("icehott-api-eu", resource.Name);
    }

    [Theory]
    [InlineData("/health", true)]
    [InlineData("/READY", true)]
    [InlineData("/release", true)]
    [InlineData("/api/workspaces", false)]
    public void Probe_Paths_Are_Not_Traced(string path, bool probe) =>
        Assert.Equal(probe, ObservabilityRegistration.IsProbe(path));
}

public sealed class TelemetryRedactionTests
{
    [Fact]
    public void Spans_Drop_Sql_And_Query_Text_And_Mask_Sensitive_Names()
    {
        var exported = new List<Activity>();
        using var source = new ActivitySource("redaction-test");
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource("redaction-test")
            .AddProcessor(new SensitiveTagRedactionProcessor())
            .AddInMemoryExporter(exported)
            .Build();

        using (var activity = source.StartActivity("op"))
        {
            activity!.SetTag("db.statement", "SELECT * FROM users WHERE password = 'hunter2'");
            activity.SetTag("db.query.text", "SELECT 1");
            activity.SetTag("url.query", "token=abc");
            activity.SetTag("url.full", "https://example.test/path?token=abc");
            activity.SetTag("url.original", "https://example.test/path?token=abc");
            activity.SetTag("http.target", "/path?token=abc");
            activity.SetTag("http.request.header.authorization", "Bearer abc");
            activity.SetTag("user.password", "hunter2");
            activity.SetTag("prompt.text", "secret prompt");
            activity.SetTag("http.request.method", "GET");
        }

        provider.ForceFlush();
        var span = Assert.Single(exported);
        var tags = span.TagObjects.ToDictionary(x => x.Key, x => x.Value?.ToString());

        Assert.DoesNotContain("db.statement", tags.Keys);
        Assert.DoesNotContain("db.query.text", tags.Keys);
        Assert.DoesNotContain("url.query", tags.Keys);
        Assert.DoesNotContain("url.full", tags.Keys);
        Assert.DoesNotContain("url.original", tags.Keys);
        Assert.DoesNotContain("http.target", tags.Keys);
        Assert.DoesNotContain("http.request.header.authorization", tags.Keys);
        Assert.Equal(TelemetryRedaction.Redacted, tags["user.password"]);
        Assert.Equal(TelemetryRedaction.Redacted, tags["prompt.text"]);
        Assert.Equal("GET", tags["http.request.method"]);
        Assert.DoesNotContain(tags.Values, v => v is not null && v.Contains("hunter2"));
    }

    [Fact]
    public void Logs_Never_Export_Exception_Text_Or_Sensitive_Attributes()
    {
        var exported = new List<LogRecord>();
        using var factory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = false;
            options.ParseStateValues = true;
            options.AddProcessor(new SensitiveLogRedactionProcessor());
            options.AddInMemoryExporter(exported);
        }));

        factory.CreateLogger("test").LogError(
            new InvalidOperationException("Host=db;Password=hunter2"),
            "Failed for {ApiKey} and {ChunkCount}",
            "sk-live-123",
            4);

        var record = Assert.Single(exported);
        Assert.Null(record.Exception);
        var attributes = record.Attributes!.ToDictionary(x => x.Key, x => x.Value?.ToString());
        Assert.Equal(TelemetryRedaction.Redacted, attributes["ApiKey"]);
        Assert.Equal("4", attributes["ChunkCount"]);
        Assert.Contains("System.InvalidOperationException", attributes["exception.type"]);
        Assert.DoesNotContain(attributes.Values, v => v is not null && (v.Contains("hunter2") || v.Contains("sk-live")));
    }
}

public sealed class TracePropagationTests
{
    [Fact]
    public async Task AiRuntimeClient_Propagates_W3C_TraceContext_To_The_AI_Service()
    {
        var exported = new List<Activity>();
        using var source = new ActivitySource("propagation-test");
        using var tracing = Sdk.CreateTracerProviderBuilder()
            .AddSource("propagation-test")
            .AddHttpClientInstrumentation()
            .AddInMemoryExporter(exported)
            .Build();

        string? receivedTraceparent = null;
        string[] receivedHeaderNames = [];
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapPost("/v1/chat", (HttpContext context) =>
        {
            receivedTraceparent = context.Request.Headers["traceparent"].ToString();
            receivedHeaderNames = context.Request.Headers.Keys.ToArray();
            return Results.Json(new { content = "ok", provider = "p", model = "m" });
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var services = new ServiceCollection();
        services.AddHttpClient<IAiRuntimeClient, AiRuntimeClient>(client =>
            client.BaseAddress = new Uri(address.TrimEnd('/') + "/"));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IAiRuntimeClient>();

        string expectedTraceId;
        using (var root = source.StartActivity("api-request"))
        {
            expectedTraceId = root!.TraceId.ToString();
            var reply = await client.ReplyAsync(new AiRuntimeRequest(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                [new AiRuntimeTurn("user", "hello")], []));
            Assert.Equal("ok", reply.Content);
        }

        Assert.NotNull(receivedTraceparent);
        var parts = receivedTraceparent!.Split('-');
        Assert.Equal(4, parts.Length);
        Assert.Equal("00", parts[0]);
        Assert.Equal(expectedTraceId, parts[1]);
        // Correlation is W3C only: no bespoke correlation header is used.
        Assert.DoesNotContain(receivedHeaderNames, h => h.StartsWith("x-icehott", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(receivedHeaderNames, h => h.Equals("authorization", StringComparison.OrdinalIgnoreCase));

        await app.StopAsync();
    }
}

public sealed class OperationalMetricsTests
{
    private sealed class Collector : IDisposable
    {
        private readonly MeterListener _listener = new();
        public List<(string Name, long Value, Dictionary<string, string?> Tags)> Longs { get; } = [];
        public List<(string Name, double Value, Dictionary<string, string?> Tags)> Doubles { get; } = [];

        public Collector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == IcehottMetrics.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) =>
            {
                lock (Longs) Longs.Add((i.Name, v, ToDictionary(tags)));
            });
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) =>
            {
                lock (Doubles) Doubles.Add((i.Name, v, ToDictionary(tags)));
            });
            _listener.Start();
        }

        private static Dictionary<string, string?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var result = new Dictionary<string, string?>();
            foreach (var tag in tags)
                result[tag.Key] = tag.Value?.ToString();
            return result;
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class ThrowingStore(Exception? fault) : IArtifactStore
    {
        public Task<ArtifactStageResult> StageAsync(Guid w, Guid a, Stream s, long m, CancellationToken c = default) =>
            fault is null ? Task.FromResult(new ArtifactStageResult("s", "o", 1, "x")) : throw fault;
        public Task CommitAsync(string s, string o, long e, string h, CancellationToken c = default) =>
            fault is null ? Task.CompletedTask : throw fault;
        public Task<ArtifactStoredObjectInfo?> GetInfoAsync(string k, CancellationToken c = default) =>
            fault is null ? Task.FromResult<ArtifactStoredObjectInfo?>(null) : throw fault;
        public Task<Stream?> OpenReadAsync(string k, CancellationToken c = default) =>
            fault is null ? Task.FromResult<Stream?>(null) : throw fault;
        public Task DeleteAsync(string k, CancellationToken c = default) =>
            fault is null ? Task.CompletedTask : throw fault;
        public Task DeleteStagingAsync(string k, CancellationToken c = default) =>
            fault is null ? Task.CompletedTask : throw fault;
        public Task<int> CleanupStagingAsync(DateTimeOffset o, int m, CancellationToken c = default) =>
            fault is null ? Task.FromResult(0) : throw fault;
    }

    [Fact]
    public async Task Artifact_Store_Decorator_Counts_Outcomes_With_Bounded_Tags_And_Rethrows()
    {
        const string provider = "metrics-test-provider";
        using var collector = new Collector();

        var ok = new InstrumentedArtifactStore(new ThrowingStore(null), provider);
        await ok.DeleteAsync("objects/x");

        var down = new InstrumentedArtifactStore(
            new ThrowingStore(new ArtifactStoreUnavailableException("secret provider detail")), provider);
        await Assert.ThrowsAsync<ArtifactStoreUnavailableException>(
            () => down.GetInfoAsync("objects/secret-key-sentinel"));

        var cancelled = new InstrumentedArtifactStore(
            new ThrowingStore(new OperationCanceledException()), provider);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.OpenReadAsync("objects/x"));

        List<(string Name, long Value, Dictionary<string, string?> Tags)> mine;
        lock (collector.Longs)
            mine = collector.Longs.Where(m => m.Tags.GetValueOrDefault("provider") == provider).ToList();

        Assert.Contains(mine, m => m.Name == "icehott.artifact.store.operations" &&
            m.Tags["operation"] == "delete" && m.Tags["outcome"] == "success");
        Assert.Contains(mine, m => m.Name == "icehott.artifact.store.operations" &&
            m.Tags["operation"] == "info" && m.Tags["outcome"] == "unavailable");
        Assert.Contains(mine, m => m.Name == "icehott.artifact.store.unavailable" &&
            m.Tags["operation"] == "info");
        Assert.Contains(mine, m => m.Tags.GetValueOrDefault("outcome") == "cancelled");

        // Cardinality/redaction: only these tag names, and no key or message ever appears as a value.
        var allowed = new HashSet<string> { "provider", "operation", "outcome" };
        Assert.All(mine, m => Assert.Subset(allowed, m.Tags.Keys.ToHashSet()));
        Assert.All(mine, m => Assert.DoesNotContain(
            m.Tags.Values, v => v is not null && (v.Contains("secret") || v.Contains("objects/"))));
    }

    [Fact]
    public async Task Ai_Runtime_Client_Records_Request_Count_And_Duration_Without_Content()
    {
        using var collector = new Collector();
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        var client = new AiRuntimeClient(new HttpClient(handler) { BaseAddress = new Uri("http://ai.test/") });

        await Assert.ThrowsAsync<AiRuntimeUnavailableException>(() => client.ReplyAsync(
            new AiRuntimeRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                [new AiRuntimeTurn("user", "prompt-sentinel-text")], [])));

        lock (collector.Longs)
            Assert.Contains(collector.Longs, m => m.Name == "icehott.ai_runtime.requests" &&
                m.Tags["operation"] == "chat" && m.Tags["outcome"] == "failure");
        lock (collector.Doubles)
            Assert.Contains(collector.Doubles, m => m.Name == "icehott.ai_runtime.request.duration" &&
                m.Tags["operation"] == "chat" && m.Value >= 0);
        lock (collector.Longs)
            Assert.All(collector.Longs, m => Assert.DoesNotContain(
                m.Tags.Values, v => v is not null && v.Contains("prompt-sentinel")));
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}

public sealed class TelemetryOutageTests
{
    [Fact]
    public async Task Unreachable_Collector_Does_Not_Break_Requests_Or_Startup()
    {
        // Port 9 (discard) refuses connections on loopback: a dead collector.
        using var factory = new IcehottApiFactory(host =>
        {
            host.UseSetting("Observability:Enabled", "true");
            host.UseSetting("Observability:Otlp:Endpoint", "http://127.0.0.1:9");
            host.UseSetting("Observability:Otlp:Protocol", "http/protobuf");
            host.UseSetting("Observability:Otlp:TimeoutSeconds", "1");
            host.UseSetting("Observability:Traces:SamplingRatio", "1");
        });

        Assert.NotNull(factory.Services.GetService<TracerProvider>());

        using var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Readiness does not depend on the telemetry pipeline.
        var release = await client.GetFromJsonAsync<Dictionary<string, string>>("/release");
        Assert.Equal("icehott-api", release!["service"]);
    }

    [Fact]
    public void Disabled_Observability_Registers_No_Exporters()
    {
        using var factory = new IcehottApiFactory();

        Assert.Null(factory.Services.GetService<TracerProvider>());
        Assert.Null(factory.Services.GetService<MeterProvider>());
    }
}

public sealed class ObservabilityCommittedConfigTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "deploy", "otel")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("deploy/otel not found.");
    }

    private static IConfiguration AppSettings(string file) =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "backend", "src", "ICEHOTT.API", file), optional: false)
            .Build();

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Production.json")]
    public void Committed_Settings_Disable_Export_And_Hold_No_Endpoint_Or_Credentials(string file)
    {
        var config = AppSettings(file);

        Assert.NotEqual("true", config["Observability:Enabled"]?.ToLowerInvariant());
        Assert.True(string.IsNullOrEmpty(config["Observability:Otlp:Endpoint"]));
        Assert.True(string.IsNullOrEmpty(config["Observability:Otlp:Headers"]));
        Assert.Empty(ObservabilityOptions.Validate(config, hosted: true));
    }

    [Fact]
    public void Committed_Base_Settings_Use_Bounded_Sampling_And_Safe_Defaults()
    {
        var options = ObservabilityOptions.Load(AppSettings("appsettings.json"));

        Assert.InRange(options.SamplingRatio, 0d, 1d);
        Assert.Equal("grpc", options.Protocol);
        Assert.False(options.Required);
    }

    [Theory]
    [InlineData("deploy/otel/collector.yaml")]
    [InlineData("deploy/otel/collector.upstream.yaml")]
    [InlineData("docker-compose.observability.yml")]
    [InlineData("deploy/otel/alerts/icehott-rules.yml")]
    public void Committed_Collector_And_Alert_Files_Contain_No_Literal_Credentials(string relative)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        Assert.DoesNotMatch(@"(?i)(bearer\s+[a-z0-9._-]{8,}|api[_-]?key\s*[:=]\s*[a-z0-9]{8,}|password\s*[:=]\s*\S+|AKIA[0-9A-Z]{12})", text);
        // Credential-bearing settings must be environment references, never literals.
        if (relative.EndsWith("collector.upstream.yaml"))
            Assert.Contains("${env:OTEL_UPSTREAM_HEADERS}", text);
    }
}
