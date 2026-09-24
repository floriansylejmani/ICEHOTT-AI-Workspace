using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Workspaces;

public sealed record WorkspaceView(Guid Id, string Name, string Slug, WorkspaceRole Role, DateTimeOffset CreatedAtUtc);
public sealed record WorkspaceResult(WorkspaceView? Workspace, string? ErrorCode)
{
    public bool Succeeded => Workspace is not null;
}
