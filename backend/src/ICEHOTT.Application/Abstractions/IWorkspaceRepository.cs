using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Abstractions;

public interface IWorkspaceRepository
{
    Task AddAsync(Workspace workspace, WorkspaceMembership ownerMembership, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkspaceMembership>> ListMembershipsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<WorkspaceMembership?> FindMembershipAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default);
}
