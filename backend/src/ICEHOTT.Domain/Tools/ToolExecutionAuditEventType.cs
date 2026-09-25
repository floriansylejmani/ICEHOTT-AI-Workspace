namespace ICEHOTT.Domain.Tools;

public enum ToolExecutionAuditEventType
{
    Requested = 1,
    Approved = 2,
    Rejected = 3,
    Started = 4,
    Succeeded = 5,
    Failed = 6,
    Cancelled = 7,
    TimedOut = 8,
    OutcomeUnknown = 9
}
