namespace ICEHOTT.API.Models;

public sealed record CreateWorkflowTriggerRequest(
    Guid WorkflowVersionId,
    string ScheduleExpression,
    string TimeZoneId);
