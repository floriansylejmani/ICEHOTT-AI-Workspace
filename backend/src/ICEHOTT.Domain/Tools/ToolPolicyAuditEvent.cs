namespace ICEHOTT.Domain.Tools;

/// <summary>
/// Append-only record of an Owner policy change: who, when, and the policy
/// before and after (JSON of <see cref="ToolPolicySettings"/>; never secrets).
/// </summary>
public sealed class ToolPolicyAuditEvent
{
    private ToolPolicyAuditEvent() { }

    public ToolPolicyAuditEvent(
        Guid id,
        Guid workspaceId,
        string toolName,
        Guid actorUserId,
        int previousVersion,
        int newVersion,
        string? previousPolicyJson,
        string newPolicyJson,
        DateTimeOffset occurredAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("ID is required.", nameof(id));
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (actorUserId == Guid.Empty) throw new ArgumentException("Actor ID is required.", nameof(actorUserId));
        if (string.IsNullOrWhiteSpace(toolName)) throw new ArgumentException("Tool name is required.", nameof(toolName));
        if (newVersion != previousVersion + 1)
            throw new ArgumentException("Policy versions advance by exactly one.", nameof(newVersion));
        if (string.IsNullOrWhiteSpace(newPolicyJson))
            throw new ArgumentException("New policy JSON is required.", nameof(newPolicyJson));

        Id = id;
        WorkspaceId = workspaceId;
        ToolName = toolName.Trim();
        ActorUserId = actorUserId;
        PreviousVersion = previousVersion;
        NewVersion = newVersion;
        PreviousPolicyJson = previousPolicyJson;
        NewPolicyJson = newPolicyJson;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string ToolName { get; private set; } = string.Empty;
    public Guid ActorUserId { get; private set; }
    public int PreviousVersion { get; private set; }
    public int NewVersion { get; private set; }
    public string? PreviousPolicyJson { get; private set; }
    public string NewPolicyJson { get; private set; } = "{}";
    public DateTimeOffset OccurredAtUtc { get; private set; }
}
