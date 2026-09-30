using System.Diagnostics.Metrics;

namespace ICEHOTT.Application.Observability;

/// <summary>
/// Operational metrics shared by the API and Worker roles. Every tag is drawn from a small,
/// closed set (operation/outcome/provider/disposition). No identifier, name, path, message or
/// payload is ever used as a tag value; see docs/PHASE-7C-OBSERVABILITY-ALERTING.md.
/// </summary>
public static class IcehottMetrics
{
    public const string MeterName = "ICEHOTT";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> ArtifactStoreOperations =
        Meter.CreateCounter<long>(
            "icehott.artifact.store.operations",
            description: "Artifact object-store operations by provider, operation and outcome.");

    public static readonly Counter<long> ArtifactStoreUnavailable =
        Meter.CreateCounter<long>(
            "icehott.artifact.store.unavailable",
            description: "Artifact object-store calls that failed because the provider was unavailable.");

    public static readonly Counter<long> AiRuntimeRequests =
        Meter.CreateCounter<long>(
            "icehott.ai_runtime.requests",
            description: "Requests from the API to the AI runtime by operation and outcome.");

    public static readonly Histogram<double> AiRuntimeRequestDuration =
        Meter.CreateHistogram<double>(
            "icehott.ai_runtime.request.duration",
            unit: "s",
            description: "Duration of requests from the API to the AI runtime.");

    public static readonly Counter<long> ReadinessChecks =
        Meter.CreateCounter<long>(
            "icehott.readiness.checks",
            description: "Readiness dependency checks by component and outcome.");

    public static readonly Counter<long> WorkflowRunsProcessed =
        Meter.CreateCounter<long>(
            "icehott.workflow.runs.processed",
            description: "Workflow run leases processed by the runner, by outcome.");

    public static readonly Counter<long> WorkflowRunnerLoopFailures =
        Meter.CreateCounter<long>(
            "icehott.workflow.runner.loop_failures",
            description: "Unexpected failures of the workflow runner loop.");

    public static readonly Counter<long> SchedulerActions =
        Meter.CreateCounter<long>(
            "icehott.workflow.scheduler.actions",
            description: "Workflow scheduler actions by disposition.");

    public static readonly Counter<long> SchedulerLoopFailures =
        Meter.CreateCounter<long>(
            "icehott.workflow.scheduler.loop_failures",
            description: "Unexpected failures of the workflow scheduler loop.");

    public static KeyValuePair<string, object?> Tag(string name, string value) => new(name, value);
}
