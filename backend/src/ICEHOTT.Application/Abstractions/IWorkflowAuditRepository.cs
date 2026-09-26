using ICEHOTT.Domain.Workflows;

namespace ICEHOTT.Application.Abstractions;

public interface IWorkflowAuditRepository
{
    Task AddAsync(
        WorkflowAuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowAuditEvent>> ListWorkspaceAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowAuditEvent>> ListRunAsync(
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken = default);
}
