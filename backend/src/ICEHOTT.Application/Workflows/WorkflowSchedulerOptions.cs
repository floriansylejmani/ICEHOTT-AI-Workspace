namespace ICEHOTT.Application.Workflows;

public sealed class WorkflowSchedulerOptions
{
    public const string SectionName = "WorkflowScheduler";

    public bool Enabled { get; set; } = true;
    public int PollMilliseconds { get; set; } = 1000;
    public int MinimumIntervalMinutes { get; set; } = 5;
    public int MaxActiveTriggersPerWorkspace { get; set; } = 50;
    public int MaxClaimsPerCycle { get; set; } = 10;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (PollMilliseconds is < 100 or > 60_000)
            errors.Add("WorkflowScheduler:PollMilliseconds must be between 100 and 60000.");

        if (MinimumIntervalMinutes is < 1 or > 1440)
            errors.Add("WorkflowScheduler:MinimumIntervalMinutes must be between 1 and 1440.");

        if (MaxActiveTriggersPerWorkspace is < 1 or > 1000)
            errors.Add("WorkflowScheduler:MaxActiveTriggersPerWorkspace must be between 1 and 1000.");

        if (MaxClaimsPerCycle is < 1 or > 100)
            errors.Add("WorkflowScheduler:MaxClaimsPerCycle must be between 1 and 100.");

        return errors;
    }
}

public sealed record WorkflowSchedulerPolicy(
    TimeSpan MinimumInterval,
    int MaxActiveTriggersPerWorkspace);
