namespace ICEHOTT.Domain.Workflows;

public enum WorkflowDefinitionStatus
{
    Draft = 1,
    Active = 2,
    Archived = 3
}

public enum WorkflowVersionStatus
{
    Draft = 1,
    Active = 2,
    Retired = 3
}

public enum WorkflowRunStatus
{
    Queued = 1,
    Running = 2,
    Waiting = 3,
    Succeeded = 4,
    Failed = 5,
    Cancelled = 6,
    OutcomeUnknown = 7
}

public enum WorkflowWaitReason
{
    Checkpoint = 1,
    Delay = 2,
    RetryBackoff = 3,
    ToolExecution = 4
}

public enum WorkflowStepType
{
    Tool = 1,
    Checkpoint = 2,
    Artifact = 3,
    Delay = 4,
    Condition = 5
}

public enum WorkflowStepRunStatus
{
    Pending = 1,
    Ready = 2,
    Running = 3,
    WaitingForCheckpoint = 4,
    WaitingForDelay = 5,
    WaitingForRetry = 6,
    WaitingForTool = 7,
    Succeeded = 8,
    Failed = 9,
    Cancelled = 10,
    Skipped = 11,
    OutcomeUnknown = 12
}

public enum WorkflowCheckpointStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Expired = 4
}

public enum ArtifactStatus
{
    Pending = 1,
    Ready = 2,
    Failed = 3,
    Deleted = 4
}

public enum WorkflowTriggerType
{
    Schedule = 1
}

public enum WorkflowTriggerFireStatus
{
    Claimed = 1,
    RunCreated = 2,
    Skipped = 3,
    Failed = 4
}

public enum WorkflowAuditEventType
{
    DefinitionCreated = 1,
    VersionCreated = 2,
    VersionActivated = 3,
    VersionRetired = 4,
    RunRequested = 5,
    RunStarted = 6,
    StepStarted = 7,
    StepSucceeded = 8,
    StepFailed = 9,
    CheckpointRequested = 10,
    CheckpointApproved = 11,
    CheckpointRejected = 12,
    ArtifactCreated = 13,
    RetryScheduled = 14,
    RunCancelled = 15,
    RunSucceeded = 16,
    RunFailed = 17,
    OutcomeUnknown = 18,
    TriggerFired = 19
}
