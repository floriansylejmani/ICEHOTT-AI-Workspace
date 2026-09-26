namespace ICEHOTT.API.Background;

public sealed class WorkflowRunnerOptions
{
    public const string SectionName = "WorkflowRunner";

    public bool Enabled { get; init; } = true;
    public int PollMilliseconds { get; init; } = 500;
    public int LeaseSeconds { get; init; } = 90;
}
