using ICEHOTT.Domain.Tools;

namespace ICEHOTT.Application.Abstractions;

public interface IWorkspaceAuditNoteRepository
{
    Task AddAsync(
        WorkspaceAuditNote note,
        CancellationToken cancellationToken = default);

    Task<WorkspaceAuditNote?> FindByExecutionAsync(
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default);
}
