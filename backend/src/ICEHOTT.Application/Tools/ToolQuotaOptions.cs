namespace ICEHOTT.Application.Tools;

public sealed class ToolQuotaOptions
{
    public const string SectionName = "ToolQuotas";

    public int DefaultPermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 60;
    public Dictionary<string, ToolQuotaLimit> Tools { get; set; } = new();

    public IReadOnlyList<string> Validate() => [];

    public ToolQuotaRule Resolve(string toolName) => throw new NotImplementedException();
}

public sealed class ToolQuotaLimit
{
    public int? PermitLimit { get; set; }
    public int? WindowSeconds { get; set; }
}

public sealed record ToolQuotaRule(int PermitLimit, int WindowSeconds)
{
    public long WindowStart(DateTimeOffset now) => throw new NotImplementedException();

    public int RetryAfterSeconds(DateTimeOffset now) => throw new NotImplementedException();
}
