namespace ICEHOTT.Domain.Workflows;

public sealed class WorkflowVersion
{
    private WorkflowVersion() { }

    public WorkflowVersion(
        Guid id,
        Guid workflowDefinitionId,
        Guid workspaceId,
        int versionNumber,
        string definitionJson,
        string definitionHash,
        Guid createdByUserId,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Workflow version ID is required.", nameof(id));
        if (workflowDefinitionId == Guid.Empty) throw new ArgumentException("Workflow definition ID is required.", nameof(workflowDefinitionId));
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (versionNumber < 1) throw new ArgumentOutOfRangeException(nameof(versionNumber));
        if (string.IsNullOrWhiteSpace(definitionJson)) throw new ArgumentException("Definition JSON is required.", nameof(definitionJson));
        if (string.IsNullOrWhiteSpace(definitionHash)) throw new ArgumentException("Definition hash is required.", nameof(definitionHash));
        if (createdByUserId == Guid.Empty) throw new ArgumentException("Creator ID is required.", nameof(createdByUserId));

        Id = id;
        WorkflowDefinitionId = workflowDefinitionId;
        WorkspaceId = workspaceId;
        VersionNumber = versionNumber;
        DefinitionJson = definitionJson;
        DefinitionHash = definitionHash.Trim();
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
        Status = WorkflowVersionStatus.Draft;
    }

    public Guid Id { get; private set; }
    public Guid WorkflowDefinitionId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public int VersionNumber { get; private set; }
    public string DefinitionJson { get; private set; } = "{}";
    public string DefinitionHash { get; private set; } = string.Empty;
    public WorkflowVersionStatus Status { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ActivatedAtUtc { get; private set; }
    public DateTimeOffset? RetiredAtUtc { get; private set; }

    public void Activate(DateTimeOffset activatedAtUtc)
    {
        if (Status != WorkflowVersionStatus.Draft)
            throw new InvalidOperationException("Only draft workflow versions can be activated.");

        Status = WorkflowVersionStatus.Active;
        ActivatedAtUtc = activatedAtUtc;
        RetiredAtUtc = null;
    }

    public void Retire(DateTimeOffset retiredAtUtc)
    {
        if (Status != WorkflowVersionStatus.Active)
            throw new InvalidOperationException("Only active workflow versions can be retired.");

        Status = WorkflowVersionStatus.Retired;
        RetiredAtUtc = retiredAtUtc;
    }
}
