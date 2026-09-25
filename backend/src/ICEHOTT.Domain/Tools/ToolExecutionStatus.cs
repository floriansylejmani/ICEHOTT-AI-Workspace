namespace ICEHOTT.Domain.Tools;

public enum ToolExecutionStatus
{
    PendingApproval = 1,
    Ready = 2,
    Running = 3,
    Succeeded = 4,
    Failed = 5,
    Rejected = 6
}
