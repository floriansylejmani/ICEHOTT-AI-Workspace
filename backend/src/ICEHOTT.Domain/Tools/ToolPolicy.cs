using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Domain.Tools;

/// <summary>
/// Owner-editable fields of a per-workspace tool policy overlay. Validation
/// against the built-in tool definition ("may only tighten") happens in the
/// application layer, which knows the registry.
/// </summary>
public sealed record ToolPolicySettings(
    bool Enabled,
    WorkspaceRole? MinimumRequesterRole,
    WorkspaceRole? MinimumApproverRole,
    bool RequiresApproval,
    int? MaxArgumentLength);

/// <summary>
/// Versioned policy overlay for one tool in one workspace. Version 0 is the
/// built-in default (no overlay); every Owner update increments the version
/// by one. <see cref="Version"/> is the optimistic concurrency token.
/// </summary>
public sealed class ToolPolicy
{
    private ToolPolicy() { }

    private ToolPolicy(Guid workspaceId, string toolName, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("Tool name is required.", nameof(toolName));

        WorkspaceId = workspaceId;
        ToolName = toolName.Trim();
        Enabled = true;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// Materialised built-in defaults (version 0). Used only as a concurrency
    /// anchor for approvals; semantically identical to "no overlay".
    /// </summary>
    public static ToolPolicy Default(Guid workspaceId, string toolName, DateTimeOffset now) =>
        new(workspaceId, toolName, now);

    public static ToolPolicy Create(
        Guid workspaceId,
        string toolName,
        ToolPolicySettings settings,
        Guid actorUserId,
        DateTimeOffset now)
    {
        var policy = new ToolPolicy(workspaceId, toolName, now);
        policy.Update(settings, actorUserId, now);
        return policy;
    }

    public Guid WorkspaceId { get; private set; }
    public string ToolName { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public bool Enabled { get; private set; }
    public WorkspaceRole? MinimumRequesterRole { get; private set; }
    public WorkspaceRole? MinimumApproverRole { get; private set; }
    public bool RequiresApproval { get; private set; }
    public int? MaxArgumentLength { get; private set; }
    public Guid? UpdatedByUserId { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public ToolPolicySettings Settings =>
        new(Enabled, MinimumRequesterRole, MinimumApproverRole, RequiresApproval, MaxArgumentLength);

    public void Update(ToolPolicySettings settings, Guid actorUserId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (actorUserId == Guid.Empty)
            throw new ArgumentException("Actor ID is required.", nameof(actorUserId));
        if (settings.MaxArgumentLength is <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "Max argument length must be positive.");

        Enabled = settings.Enabled;
        MinimumRequesterRole = settings.MinimumRequesterRole;
        MinimumApproverRole = settings.MinimumApproverRole;
        RequiresApproval = settings.RequiresApproval;
        MaxArgumentLength = settings.MaxArgumentLength;
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = now;
        Version = checked(Version + 1);
    }
}

/// <summary>
/// Effective (built-in + overlay) policy. Also the shape snapshotted on each
/// execution at admission time.
/// </summary>
public sealed record EffectiveToolPolicy(
    int Version,
    bool Enabled,
    WorkspaceRole MinimumRequesterRole,
    WorkspaceRole? MinimumApproverRole,
    bool RequiresApproval,
    int? MaxArgumentLength)
{
    /// <summary>
    /// Combines an admission snapshot with the current policy, keeping the
    /// stricter value of every field. Enabled comes from <paramref name="current"/>:
    /// a tool disabled now is disabled, whatever the snapshot said.
    /// </summary>
    public EffectiveToolPolicy StricterOf(EffectiveToolPolicy current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return new EffectiveToolPolicy(
            current.Version,
            current.Enabled,
            Max(MinimumRequesterRole, current.MinimumRequesterRole),
            MaxNullable(MinimumApproverRole, current.MinimumApproverRole),
            RequiresApproval || current.RequiresApproval,
            MinNullable(MaxArgumentLength, current.MaxArgumentLength));
    }

    private static WorkspaceRole Max(WorkspaceRole a, WorkspaceRole b) => a >= b ? a : b;

    private static WorkspaceRole? MaxNullable(WorkspaceRole? a, WorkspaceRole? b) =>
        a is null ? b : b is null ? a : Max(a.Value, b.Value);

    private static int? MinNullable(int? a, int? b) =>
        a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}
