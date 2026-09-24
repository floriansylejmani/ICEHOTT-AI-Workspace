namespace ICEHOTT.Infrastructure.Ai;

public sealed class AiRuntimeOptions
{
    public const string SectionName = "AiRuntime";

    public string BaseUrl { get; init; } = "http://localhost:8000";
    public int TimeoutSeconds { get; init; } = 30;
}
