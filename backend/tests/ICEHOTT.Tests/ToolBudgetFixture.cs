using System.Collections.Concurrent;
using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Tools;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ICEHOTT.Tests;

/// <summary>
/// Phase 4.5 packet C fixture: the API with a frozen, manually advanced clock
/// (quota windows are deterministic), a small quota on test tools, captured
/// logs, and test-only tools that fail or answer with secret-bearing content.
/// </summary>
public sealed class ToolBudgetFixture : ToolSecurityFixture
{
    public const string QuotaTool = "test.quota";
    public const int QuotaToolLimit = 3;
    public const int WindowSeconds = 3600;
    public const int NoteLimit = 2;

    public ToolBudgetFixture()
        : this(new ManualTimeProvider(DateTimeOffset.UtcNow), new CapturingLoggerProvider())
    {
    }

    private ToolBudgetFixture(ManualTimeProvider clock, CapturingLoggerProvider logs)
        : base(CreateFactory(clock, logs))
    {
        Clock = clock;
        Logs = logs;
    }

    public ManualTimeProvider Clock { get; }
    public CapturingLoggerProvider Logs { get; }

    private static IcehottApiFactory CreateFactory(
        ManualTimeProvider fixtureClock,
        CapturingLoggerProvider fixtureLogs) =>
         new IcehottApiFactory(builder =>
        {
            builder.UseSetting("ToolQuotas:DefaultPermitLimit", "1000");
            builder.UseSetting("ToolQuotas:WindowSeconds", WindowSeconds.ToString());
            builder.UseSetting($"ToolQuotas:Tools:{QuotaTool}:PermitLimit", QuotaToolLimit.ToString());
            builder.UseSetting("ToolQuotas:Tools:workspace.audit-note.create:PermitLimit", NoteLimit.ToString());
            builder.UseSetting("Logging:LogLevel:Default", "Trace");
            builder.UseSetting("Logging:LogLevel:Microsoft", "Trace");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(fixtureClock);
                services.AddSingleton<ILoggerProvider>(fixtureLogs);
                services.AddScoped<IWorkspaceTool, QuotaProbeTool>();
                services.AddScoped<IWorkspaceTool, SecretFailureTool>();
                services.AddScoped<IWorkspaceTool, SecretResultTool>();
            });
        });

    public async Task<int> QuotaUsedAsync(Guid workspaceId, string toolName)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        var counter = await db.ToolQuotaCounters.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.ToolName == toolName);
        return counter?.Count ?? 0;
    }

    /// <summary>Everything persisted for a workspace's tool executions, as one string.</summary>
    public async Task<string> PersistedToolDataAsync(Guid workspaceId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        var executions = await db.ToolExecutions.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .ToListAsync();
        var audit = await db.ToolExecutionAuditEvents.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .ToListAsync();

        return JsonSerializer.Serialize(new { executions, audit });
    }
}

/// <summary>Read-only test tool with a small configured quota.</summary>
public sealed class QuotaProbeTool : IWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        ToolBudgetFixture.QuotaTool,
        "Test-only tool with a small request quota.",
        ToolRiskLevel.ReadOnly,
        WorkspaceRole.Member,
        RequiresApproval: false,
        MinimumApproverRole: null,
        [new ToolArgumentDefinition("text", ToolArgumentType.String, Required: true, MaxLength: 100)]);

    public ToolArgumentValidationResult ValidateArguments(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty("text", out var text) &&
        text.ValueKind == JsonValueKind.String &&
        arguments.EnumerateObject().Count() == 1
            ? ToolArgumentValidationResult.Valid
            : ToolArgumentValidationResult.Invalid("Argument 'text' is required.");

    public Task<ToolExecutionOutput> ExecuteAsync(
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ToolExecutionOutput("""{"ok":true}"""));
}

/// <summary>
/// Simulates an upstream failure whose exception text, inner exception and
/// Data carry a credential the handler obtained elsewhere (not from its input).
/// </summary>
public sealed class SecretFailureTool : IWorkspaceTool
{
    public const string Name = "test.secret-failure";

    public ToolDefinition Definition { get; } = new(
        Name,
        "Test-only tool whose exception carries a secret.",
        ToolRiskLevel.ReadOnly,
        WorkspaceRole.Member,
        RequiresApproval: false,
        MinimumApproverRole: null,
        [new ToolArgumentDefinition("text", ToolArgumentType.String, Required: true, MaxLength: 100)]);

    public ToolArgumentValidationResult ValidateArguments(JsonElement arguments) =>
        ToolArgumentValidationResult.Valid;

    public Task<ToolExecutionOutput> ExecuteAsync(
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var inner = new HttpRequestException(
            $"401 from upstream; retried with Authorization: Bearer {TestSecrets.BearerValue}");
        var exception = new InvalidOperationException(
            $"Upstream rejected key {TestSecrets.OpenAiKey} (password={TestSecrets.Password})",
            inner);
        exception.Data["apiKey"] = TestSecrets.GitHubToken;
        throw exception;
    }
}

/// <summary>Returns a result that embeds credentials (e.g. echoed by an upstream API).</summary>
public sealed class SecretResultTool : IWorkspaceTool
{
    public const string Name = "test.secret-result";

    public ToolDefinition Definition { get; } = new(
        Name,
        "Test-only tool whose result carries secrets.",
        ToolRiskLevel.ReadOnly,
        WorkspaceRole.Member,
        RequiresApproval: false,
        MinimumApproverRole: null,
        [new ToolArgumentDefinition("text", ToolArgumentType.String, Required: true, MaxLength: 100)]);

    public ToolArgumentValidationResult ValidateArguments(JsonElement arguments) =>
        ToolArgumentValidationResult.Valid;

    public Task<ToolExecutionOutput> ExecuteAsync(
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ToolExecutionOutput(JsonSerializer.Serialize(new
        {
            status = "ok",
            apiKey = "plain-value-without-pattern",
            nested = new { clientSecret = "another-plain-value", note = $"use {TestSecrets.GitHubToken} next time" },
            items = new[] { $"Bearer {TestSecrets.BearerValue}", "harmless" }
        })));
}

/// <summary>
/// Representative credentials, assembled at runtime so no literal token is
/// committed (and repository secret scanning is not tripped). None is real.
/// </summary>
public static class TestSecrets
{
    private static string Repeat(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    public static readonly string GitHubToken = "gh" + "p_" + Repeat("aB3d", 9);
    public static readonly string GitHubFineGrained = "github" + "_pat_" + Repeat("11ABCDE0", 3) + "_" + Repeat("xY9z", 12);
    public static readonly string AwsAccessKeyId = "AK" + "IA" + "Q7" + Repeat("ZX4W", 3) + "R2";
    public static readonly string OpenAiKey = "s" + "k-proj-" + Repeat("Tq8vN2", 6);
    public static readonly string AnthropicKey = "s" + "k-ant-api03-" + Repeat("Hq7_Lp2", 6);
    public static readonly string SlackToken = "xo" + "xb-" + "1234567890-" + Repeat("aZ9", 8);
    public static readonly string StripeKey = "s" + "k_live_" + Repeat("4eC39HqLyjWDarjt", 2);
    public static readonly string GoogleApiKey = "AI" + "za" + Repeat("Sy7_kQ", 5) + "Ab-Z9";
    public static readonly string Jwt =
        "ey" + "JhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9" + "." +
        "ey" + "JzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4ifQ" + "." +
        "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
    public static readonly string PemBody = "MIIEow" + "IBAAKCAQEA" + Repeat("q8Lz", 8);
    public static readonly string PemBlock =
        "-----BEGIN " + "RSA PRIVATE KEY-----\n" + PemBody + "\n-----END " + "RSA PRIVATE KEY-----";
    public static readonly string BearerValue = Repeat("Zm9vYmFy", 4) + "Q";
    public static readonly string Password = "Tr0ub4dor&3-" + Repeat("x", 6);
    public static readonly string UrlPassword = "S3cr3t" + "Pa55w0rd";
    public static readonly string UrlWithCredentials = "postgres://svc_user:" + UrlPassword + "@db.internal:5432/app";
}

public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
}

/// <summary>Collects every formatted log line (message, exception text and state values).</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines.ToArray();

    public string All => string.Join('\n', _lines);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" | ", pairs.Select(x => $"{x.Key}={x.Value}"))
                : string.Empty;
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} [{values}] {exception}");
        }
    }
}
