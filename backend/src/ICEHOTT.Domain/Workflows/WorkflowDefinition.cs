using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Domain.Workflows;

public sealed class WorkflowDefinition
{
    private WorkflowDefinition() { }

    public WorkflowDefinition(
        Guid id,
        Guid workspaceId,
        string name,
        string? description,
        WorkspaceRole minimumRunRole,
        Guid createdByUserId,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Workflow definition ID is required.", nameof(id));
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (createdByUserId == Guid.Empty) throw new ArgumentException("Creator ID is required.", nameof(createdByUserId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Workflow name is required.", nameof(name));
        if (minimumRunRole is < WorkspaceRole.Member or > WorkspaceRole.Owner)
            throw new ArgumentOutOfRangeException(nameof(minimumRunRole));

        Id = id;
        WorkspaceId = workspaceId;
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        MinimumRunRole = minimumRunRole;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        Status = WorkflowDefinitionStatus.Draft;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public WorkflowDefinitionStatus Status { get; private set; }
    public WorkspaceRole MinimumRunRole { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void UpdateDraft(
        string name,
        string? description,
        WorkspaceRole minimumRunRole,
        DateTimeOffset updatedAtUtc)
    {
        if (Status != WorkflowDefinitionStatus.Draft)
            throw new InvalidOperationException("Only draft workflow definitions can be edited.");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Workflow name is required.", nameof(name));
        if (minimumRunRole is < WorkspaceRole.Member or > WorkspaceRole.Owner)
            throw new ArgumentOutOfRangeException(nameof(minimumRunRole));

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        MinimumRunRole = minimumRunRole;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void MarkActive(DateTimeOffset updatedAtUtc)
    {
        if (Status == WorkflowDefinitionStatus.Archived)
            throw new InvalidOperationException("Archived workflow definitions cannot be activated.");

        Status = WorkflowDefinitionStatus.Active;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Archive(DateTimeOffset updatedAtUtc)
    {
        if (Status == WorkflowDefinitionStatus.Archived)
            return;

        Status = WorkflowDefinitionStatus.Archived;
        UpdatedAtUtc = updatedAtUtc;
    }
}
