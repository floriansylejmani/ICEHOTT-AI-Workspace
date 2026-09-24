using System.Text;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Workspaces;

public sealed class WorkspaceService(
    IWorkspaceRepository workspaces,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<WorkspaceView> CreateAsync(Guid userId, string name, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var id = Guid.NewGuid();
        var workspace = new Workspace(id, name, BuildSlug(name), userId, now);
        var membership = new WorkspaceMembership(id, userId, WorkspaceRole.Owner, now);

        await workspaces.AddAsync(workspace, membership, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Map(membership, workspace);
    }

    public async Task<IReadOnlyList<WorkspaceView>> ListAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var memberships = await workspaces.ListMembershipsAsync(userId, cancellationToken);
        return memberships.Select(x => Map(x, x.Workspace)).ToArray();
    }

    public async Task<WorkspaceResult> GetAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var membership = await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken);
        return membership is null
            ? new(null, "workspace_not_found")
            : new(Map(membership, membership.Workspace), null);
    }

    private static WorkspaceView Map(WorkspaceMembership membership, Workspace workspace) =>
        new(workspace.Id, workspace.Name, workspace.Slug, membership.Role, workspace.CreatedAtUtc);

    private static string BuildSlug(string name)
    {
        var builder = new StringBuilder();
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var prefix = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(prefix)) prefix = "workspace";
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{prefix}-{suffix}";
    }
}
