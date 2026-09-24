using ICEHOTT.Domain.Users;

namespace ICEHOTT.Domain.Workspaces;

public sealed class WorkspaceMembership
{
    private WorkspaceMembership() { }

    public WorkspaceMembership(Guid workspaceId, Guid userId, WorkspaceRole role, DateTimeOffset joinedAtUtc)
    {
        WorkspaceId = workspaceId;
        UserId = userId;
        Role = role;
        JoinedAtUtc = joinedAtUtc;
    }

    public Guid WorkspaceId { get; private set; }
    public Guid UserId { get; private set; }
    public WorkspaceRole Role { get; private set; }
    public DateTimeOffset JoinedAtUtc { get; private set; }
    public Workspace Workspace { get; private set; } = null!;
    public User User { get; private set; } = null!;
}
