namespace ICEHOTT.API.Background;

public sealed class KnowledgeWorkerOptions
{
    public const string SectionName = "KnowledgeWorker";

    public bool Enabled { get; init; } = true;
    public int PollMilliseconds { get; init; } = 500;
    public int LeaseSeconds { get; init; } = 120;
    public int MaxRetryDelaySeconds { get; init; } = 300;
}
