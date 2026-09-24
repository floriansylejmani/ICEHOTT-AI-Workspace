using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class WorkspaceRepository(ICEHOTTDbContext db) : IWorkspaceRepository
{
    public async Task AddAsync(Workspace workspace, WorkspaceMembership ownerMembership, CancellationToken cancellationToken = default)
    {
        await db.Workspaces.AddAsync(workspace, cancellationToken);
        await db.WorkspaceMemberships.AddAsync(ownerMembership, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkspaceMembership>> ListMembershipsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await db.WorkspaceMemberships.Include(x => x.Workspace)
            .Where(x => x.UserId == userId).OrderBy(x => x.Workspace.Name).ToListAsync(cancellationToken);

    public Task<WorkspaceMembership?> FindMembershipAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) =>
        db.WorkspaceMemberships.Include(x => x.Workspace)
            .SingleOrDefaultAsync(x => x.UserId == userId && x.WorkspaceId == workspaceId, cancellationToken);
}
