using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Tools;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class WorkspaceAuditNoteRepository(ICEHOTTDbContext db)
    : IWorkspaceAuditNoteRepository
{
    public Task AddAsync(
        WorkspaceAuditNote note,
        CancellationToken cancellationToken = default) =>
        db.WorkspaceAuditNotes.AddAsync(note, cancellationToken).AsTask();

    public Task<WorkspaceAuditNote?> FindByExecutionAsync(
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default) =>
        db.WorkspaceAuditNotes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.WorkspaceId == workspaceId &&
                     x.ToolExecutionId == executionId,
                cancellationToken);
}
