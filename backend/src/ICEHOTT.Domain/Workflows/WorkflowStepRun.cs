namespace ICEHOTT.Domain.Workflows;

public sealed class WorkflowStepRun
{
    private WorkflowStepRun() { }

    public WorkflowStepRun(
        Guid id,
        Guid workflowRunId,
        Guid workspaceId,
        string stepKey,
        int attempt,
        WorkflowStepType stepType,
        string inputJson)
    {
        if (id == Guid.Empty) throw new ArgumentException("Workflow step run ID is required.", nameof(id));
        if (workflowRunId == Guid.Empty) throw new ArgumentException("Workflow run ID is required.", nameof(workflowRunId));
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(stepKey)) throw new ArgumentException("Step key is required.", nameof(stepKey));
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        if (string.IsNullOrWhiteSpace(inputJson)) throw new ArgumentException("Input JSON is required.", nameof(inputJson));

        Id = id;
        WorkflowRunId = workflowRunId;
        WorkspaceId = workspaceId;
        StepKey = stepKey.Trim();
        Attempt = attempt;
        StepType = stepType;
        InputJson = inputJson;
        Status = WorkflowStepRunStatus.Pending;
    }

    public Guid Id { get; private set; }
    public Guid WorkflowRunId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string StepKey { get; private set; } = string.Empty;
    public int Attempt { get; private set; }
    public WorkflowStepType StepType { get; private set; }
    public WorkflowStepRunStatus Status { get; private set; }
    public string InputJson { get; private set; } = "{}";
    public string? OutputJson { get; private set; }
    public Guid? ToolExecutionId { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void MarkReady()
    {
        if (Status != WorkflowStepRunStatus.Pending)
            throw new InvalidOperationException("Only pending workflow steps can become ready.");
        Status = WorkflowStepRunStatus.Ready;
    }

    public void Start(DateTimeOffset startedAtUtc, Guid? toolExecutionId = null)
    {
        if (Status != WorkflowStepRunStatus.Ready)
            throw new InvalidOperationException("Only ready workflow steps can start.");

        Status = WorkflowStepRunStatus.Running;
        StartedAtUtc = startedAtUtc;
        ToolExecutionId = toolExecutionId;
    }

    public void WaitForCheckpoint()
    {
        RequireRunning();
        Status = WorkflowStepRunStatus.WaitingForCheckpoint;
    }

    public void WaitForDelay(DateTimeOffset resumeAtUtc)
    {
        RequireRunning();
        Status = WorkflowStepRunStatus.WaitingForDelay;
        NextAttemptAtUtc = resumeAtUtc;
    }

    public void WaitForTool(Guid toolExecutionId)
    {
        RequireRunning();
        if (toolExecutionId == Guid.Empty)
            throw new ArgumentException("Tool execution ID is required.", nameof(toolExecutionId));

        ToolExecutionId = toolExecutionId;
        Status = WorkflowStepRunStatus.WaitingForTool;
    }

    public void AttachToolExecution(Guid toolExecutionId)
    {
        RequireRunning();
        if (toolExecutionId == Guid.Empty)
            throw new ArgumentException("Tool execution ID is required.", nameof(toolExecutionId));
        if (ToolExecutionId is not null && ToolExecutionId != toolExecutionId)
            throw new InvalidOperationException("Workflow step is already linked to another tool execution.");

        ToolExecutionId = toolExecutionId;
    }

    public void WaitForRetry(DateTimeOffset retryAtUtc, string errorCode, string? errorMessage)
    {
        if (Status is not (WorkflowStepRunStatus.Running or WorkflowStepRunStatus.WaitingForTool))
            throw new InvalidOperationException("Only active workflow steps can wait for retry.");
        if (string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("Error code is required.", nameof(errorCode));

        Status = WorkflowStepRunStatus.WaitingForRetry;
        NextAttemptAtUtc = retryAtUtc;
        ErrorCode = errorCode.Trim();
        ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim();
    }

    public void Succeed(string? outputJson, DateTimeOffset completedAtUtc)
    {
        if (Status is not (WorkflowStepRunStatus.Running or WorkflowStepRunStatus.WaitingForCheckpoint or WorkflowStepRunStatus.WaitingForDelay or WorkflowStepRunStatus.WaitingForTool))
            throw new InvalidOperationException("Only active workflow steps can succeed.");

        Status = WorkflowStepRunStatus.Succeeded;
        OutputJson = outputJson;
        CompletedAtUtc = completedAtUtc;
        NextAttemptAtUtc = null;
        ErrorCode = null;
        ErrorMessage = null;
    }

    public void Fail(string errorCode, string? errorMessage, DateTimeOffset completedAtUtc)
    {
        if (Status is WorkflowStepRunStatus.Succeeded or WorkflowStepRunStatus.Failed or WorkflowStepRunStatus.Cancelled or WorkflowStepRunStatus.Skipped or WorkflowStepRunStatus.OutcomeUnknown)
            throw new InvalidOperationException("Terminal workflow step cannot fail.");
        if (string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("Error code is required.", nameof(errorCode));

        Status = WorkflowStepRunStatus.Failed;
        CompletedAtUtc = completedAtUtc;
        NextAttemptAtUtc = null;
        ErrorCode = errorCode.Trim();
        ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim();
    }

    public void CloseForRetry(DateTimeOffset completedAtUtc)
    {
        if (Status != WorkflowStepRunStatus.WaitingForRetry)
            throw new InvalidOperationException("Only retry-waiting workflow steps can close for retry.");

        Status = WorkflowStepRunStatus.Failed;
        CompletedAtUtc = completedAtUtc;
        NextAttemptAtUtc = null;
    }

    public void Cancel(DateTimeOffset completedAtUtc)
    {
        if (Status is WorkflowStepRunStatus.Succeeded or WorkflowStepRunStatus.Failed or WorkflowStepRunStatus.Cancelled or WorkflowStepRunStatus.Skipped or WorkflowStepRunStatus.OutcomeUnknown)
            throw new InvalidOperationException("Terminal workflow step cannot be cancelled.");

        Status = WorkflowStepRunStatus.Cancelled;
        CompletedAtUtc = completedAtUtc;
        NextAttemptAtUtc = null;
    }

    public void Skip(DateTimeOffset completedAtUtc)
    {
        if (Status is not (WorkflowStepRunStatus.Pending or WorkflowStepRunStatus.Ready))
            throw new InvalidOperationException("Only pending or ready workflow steps can be skipped.");

        Status = WorkflowStepRunStatus.Skipped;
        CompletedAtUtc = completedAtUtc;
    }

    public void MarkOutcomeUnknown(string? errorMessage, DateTimeOffset completedAtUtc)
    {
        if (Status is not (WorkflowStepRunStatus.Running or WorkflowStepRunStatus.WaitingForTool))
            throw new InvalidOperationException("Only active tool workflow steps can have an unknown outcome.");

        Status = WorkflowStepRunStatus.OutcomeUnknown;
        CompletedAtUtc = completedAtUtc;
        ErrorCode = "workflow_step_outcome_unknown";
        ErrorMessage = string.IsNullOrWhiteSpace(errorMessage)
            ? "Workflow step outcome requires operator review."
            : errorMessage.Trim();
    }

    private void RequireRunning()
    {
        if (Status != WorkflowStepRunStatus.Running)
            throw new InvalidOperationException("Workflow step must be running.");
    }
}
