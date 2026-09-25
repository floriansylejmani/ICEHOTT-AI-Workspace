namespace ICEHOTT.Infrastructure.Ai;

public sealed class OpenAiEmbeddingOptions
{
    public const string SectionName = "OpenAiEmbedding";

    public string BaseUrl { get; init; } = "https://api.openai.com/v1/";
    public string ApiKey { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 30;
}
