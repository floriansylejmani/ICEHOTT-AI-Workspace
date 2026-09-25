namespace ICEHOTT.Infrastructure.Ai;

public sealed class OpenAiSemanticEvaluationOptions
{
    public string BaseUrl { get; init; } = "https://api.openai.com/v1/";
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gpt-6-luna";
    public int TimeoutSeconds { get; init; } = 60;
    public int MaxInputBytes { get; init; } = 8192;
    public int MaxOutputTokens { get; init; } = 256;
}
