using ICEHOTT.Domain.Tools;

namespace ICEHOTT.Application.Abstractions;

public interface IToolExecutionRepository
{
    Task<ToolExecution?> FindAsync(
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default);

    Task<ToolExecution?> FindByIdempotencyAsync(
        Guid workspaceId,
        string toolName,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ToolExecution>> ListAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ToolExecutionAuditEvent>> ListAuditEventsAsync(
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        ToolExecution execution,
        CancellationToken cancellationToken = default);

    Task AddAuditEventAsync(
        ToolExecutionAuditEvent auditEvent,
        CancellationToken cancellationToken = default);
}
