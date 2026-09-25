namespace ICEHOTT.Application.Tools;

/// <summary>
/// Phase 4.5 packet C request quotas (docs/PHASE-4.5-C-BUDGETS-SECRETS.md C1).
/// A quota bounds how many executions one workspace may admit for one tool per
/// fixed window. Server configuration only: clients and prompts cannot set it.
/// </summary>
public sealed class ToolQuotaOptions
{
    public const string SectionName = "ToolQuotas";
    public const int MaxPermitLimit = 1_000_000;
    public const int MaxWindowSeconds = 86_400;

    public int DefaultPermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 60;

    /// <summary>Per-tool overrides keyed by tool name (case-insensitive).</summary>
    public Dictionary<string, ToolQuotaLimit> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        Check("DefaultPermitLimit", DefaultPermitLimit, MaxPermitLimit, errors);
        Check("WindowSeconds", WindowSeconds, MaxWindowSeconds, errors);

        foreach (var (tool, limit) in Tools)
        {
            if (string.IsNullOrWhiteSpace(tool))
                errors.Add("Tool quota overrides need a tool name.");
            if (limit.PermitLimit is { } permit)
                Check($"Tools:{tool}:PermitLimit", permit, MaxPermitLimit, errors);
            if (limit.WindowSeconds is { } window)
                Check($"Tools:{tool}:WindowSeconds", window, MaxWindowSeconds, errors);
        }

        return errors;
    }

    public ToolQuotaRule Resolve(string toolName)
    {
        var limit = Tools
            .FirstOrDefault(x => string.Equals(x.Key, toolName, StringComparison.OrdinalIgnoreCase))
            .Value;

        return new ToolQuotaRule(
            limit?.PermitLimit ?? DefaultPermitLimit,
            limit?.WindowSeconds ?? WindowSeconds);
    }

    private static void Check(string name, int value, int max, List<string> errors)
    {
        if (value < 1 || value > max)
            errors.Add($"ToolQuotas:{name} must be between 1 and {max}.");
    }
}

public sealed class ToolQuotaLimit
{
    public int? PermitLimit { get; set; }
    public int? WindowSeconds { get; set; }
}

/// <summary>Effective quota for one tool: at most <see cref="PermitLimit"/> admissions per aligned window.</summary>
public sealed record ToolQuotaRule(int PermitLimit, int WindowSeconds)
{
    /// <summary>Start of the window containing <paramref name="now"/>, in Unix seconds (windows are epoch-aligned).</summary>
    public long WindowStart(DateTimeOffset now) =>
        UnixTicks(now) / TimeSpan.TicksPerSecond / WindowSeconds * WindowSeconds;

    /// <summary>Whole seconds until the next window opens; always at least 1.</summary>
    public int RetryAfterSeconds(DateTimeOffset now)
    {
        var windowEndTicks = (WindowStart(now) + WindowSeconds) * TimeSpan.TicksPerSecond;
        var remaining = windowEndTicks - UnixTicks(now);
        return (int)Math.Max(1, (remaining + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);
    }

    private static long UnixTicks(DateTimeOffset now) =>
        now.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
}
