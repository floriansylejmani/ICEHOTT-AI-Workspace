using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Tools;

public sealed record EffectiveToolPolicyView(
    int Version,
    bool Enabled,
    WorkspaceRole MinimumRequesterRole,
    WorkspaceRole? MinimumApproverRole,
    bool RequiresApproval,
    int? MaxArgumentLength);

public sealed record ToolPolicyView(
    string ToolName,
    ToolRiskLevel RiskLevel,
    int Version,
    bool Enabled,
    WorkspaceRole? MinimumRequesterRole,
    WorkspaceRole? MinimumApproverRole,
    bool RequiresApproval,
    int? MaxArgumentLength,
    Guid? UpdatedByUserId,
    DateTimeOffset? UpdatedAtUtc,
    EffectiveToolPolicyView Effective);

public sealed record ToolPolicyAuditEventView(
    int PreviousVersion,
    int NewVersion,
    Guid ActorUserId,
    DateTimeOffset OccurredAtUtc,
    JsonElement? PreviousPolicy,
    JsonElement NewPolicy);

/// <summary>
/// Owner-managed per-workspace tool policy overlay (Phase 4.5 packet B).
/// Membership and role are always resolved from the database; the request body
/// is parsed strictly and can only tighten the built-in definition.
/// </summary>
public sealed class ToolPolicyService(
    IWorkspaceRepository workspaces,
    IToolPolicyRepository policies,
    IToolRegistry registry,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "expectedVersion", "enabled", "minimumRequesterRole",
        "minimumApproverRole", "requiresApproval", "maxArgumentLength"
    };

    public async Task<ToolOperationResult<IReadOnlyList<ToolPolicyView>>> ListAsync(
        Guid userId,
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        var access = await AuthorizeAsync(userId, workspaceId, WorkspaceRole.Admin, cancellationToken);
        if (access is not null)
            return new(null, access);

        var overlays = (await policies.ListAsync(workspaceId, cancellationToken))
            .ToDictionary(x => x.ToolName, StringComparer.Ordinal);

        var views = registry.All
            .Select(tool => Map(tool.Definition, overlays.GetValueOrDefault(tool.Definition.Name)))
            .ToArray();

        return new(views, null);
    }

    public async Task<ToolOperationResult<ToolPolicyView>> GetAsync(
        Guid userId,
        Guid workspaceId,
        string toolName,
        CancellationToken cancellationToken = default)
    {
        var access = await AuthorizeAsync(userId, workspaceId, WorkspaceRole.Admin, cancellationToken);
        if (access is not null)
            return new(null, access);

        var tool = registry.Find(toolName);
        if (tool is null)
            return new(null, "tool_not_found");

        var overlay = await policies.FindAsync(workspaceId, tool.Definition.Name, cancellationToken);
        return new(Map(tool.Definition, overlay), null);
    }

    public async Task<ToolOperationResult<IReadOnlyList<ToolPolicyAuditEventView>>> ListAuditAsync(
        Guid userId,
        Guid workspaceId,
        string toolName,
        CancellationToken cancellationToken = default)
    {
        var access = await AuthorizeAsync(userId, workspaceId, WorkspaceRole.Owner, cancellationToken);
        if (access is not null)
            return new(null, access);

        var tool = registry.Find(toolName);
        if (tool is null)
            return new(null, "tool_not_found");

        var events = await policies.ListAuditEventsAsync(workspaceId, tool.Definition.Name, cancellationToken);
        var views = events
            .OrderBy(x => x.NewVersion)
            .Select(x => new ToolPolicyAuditEventView(
                x.PreviousVersion,
                x.NewVersion,
                x.ActorUserId,
                x.OccurredAtUtc,
                x.PreviousPolicyJson is null ? null : Parse(x.PreviousPolicyJson),
                Parse(x.NewPolicyJson)))
            .ToArray();

        return new(views, null);
    }

    public async Task<ToolOperationResult<ToolPolicyView>> UpdateAsync(
        Guid userId,
        Guid workspaceId,
        string toolName,
        JsonElement body,
        CancellationToken cancellationToken = default)
    {
        var access = await AuthorizeAsync(userId, workspaceId, WorkspaceRole.Owner, cancellationToken);
        if (access is not null)
            return new(null, access);

        var tool = registry.Find(toolName);
        if (tool is null)
            return new(null, "tool_not_found");

        var parsed = Parse(body);
        if (parsed.Error is not null)
            return new(null, parsed.Error.Value.Code, parsed.Error.Value.Errors);

        var (expectedVersion, settings) = parsed.Value!.Value;
        var invalid = ToolPolicyEvaluator.Validate(tool.Definition, settings);
        if (invalid is not null)
            return new(null, invalid.Value.Code, invalid.Value.Errors);

        var name = tool.Definition.Name;
        var existing = await policies.FindAsync(workspaceId, name, cancellationToken);
        var currentVersion = existing?.Version ?? 0;
        if (expectedVersion != currentVersion)
            return new(null, "policy_version_conflict", [$"Current policy version is {currentVersion}."]);

        // A materialised version-0 row is "built-in defaults": no previous overlay.
        var previousJson = existing is null || existing.Version == 0
            ? null
            : JsonSerializer.Serialize(existing.Settings, AuditJson);

        var now = clock.GetUtcNow();
        ToolPolicy policy;
        if (existing is null)
        {
            policy = ToolPolicy.Create(workspaceId, name, settings, userId, now);
            await policies.AddAsync(policy, cancellationToken);
        }
        else
        {
            policy = existing;
            policy.Update(settings, userId, now);
        }

        await policies.AddAuditEventAsync(
            new ToolPolicyAuditEvent(
                Guid.NewGuid(),
                workspaceId,
                name,
                userId,
                currentVersion,
                policy.Version,
                previousJson,
                JsonSerializer.Serialize(policy.Settings, AuditJson),
                now),
            cancellationToken);

        // Version is the concurrency token: a concurrent update (or concurrent
        // first insert) makes this commit fail instead of overwriting.
        if (await policies.SaveChangesAsync(cancellationToken) != ToolPersistenceOutcome.Saved)
            return new(null, "policy_version_conflict", ["The policy changed concurrently; reload and retry."]);

        return new(Map(tool.Definition, policy), null);
    }

    private async Task<string?> AuthorizeAsync(
        Guid userId,
        Guid workspaceId,
        WorkspaceRole minimumRole,
        CancellationToken cancellationToken)
    {
        var membership = await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken);
        if (membership is null)
            return "workspace_not_found";

        return membership.Role < minimumRole ? "forbidden" : null;
    }

    private static ToolPolicyView Map(ToolDefinition definition, ToolPolicy? overlay)
    {
        var effective = ToolPolicyEvaluator.Effective(definition, overlay);
        return new ToolPolicyView(
            definition.Name,
            definition.RiskLevel,
            overlay?.Version ?? 0,
            overlay?.Enabled ?? true,
            overlay?.MinimumRequesterRole,
            overlay?.MinimumApproverRole,
            overlay?.RequiresApproval ?? false,
            overlay?.MaxArgumentLength,
            overlay?.UpdatedByUserId,
            overlay?.UpdatedAtUtc,
            new EffectiveToolPolicyView(
                effective.Version,
                effective.Enabled,
                effective.MinimumRequesterRole,
                effective.MinimumApproverRole,
                effective.RequiresApproval,
                effective.MaxArgumentLength));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private readonly record struct ParseResult(
        (int ExpectedVersion, ToolPolicySettings Settings)? Value,
        (string Code, string[] Errors)? Error);

    private static ParseResult Invalid(string message) =>
        new(null, ("invalid_policy", [message]));

    /// <summary>
    /// Strict body parser: object only, no duplicate or unknown properties,
    /// exact JSON types, role names as exact strings (never numbers).
    /// </summary>
    private static ParseResult Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return Invalid("Policy body must be a JSON object.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return Invalid($"Duplicate property '{property.Name}'.");

            // Risk level is fixed by the built-in definition; asking to change
            // it is a downgrade attempt, not a typo.
            if (string.Equals(property.Name, "riskLevel", StringComparison.OrdinalIgnoreCase))
                return new(null, ("policy_downgrade_rejected", ["riskLevel is fixed by the tool definition."]));

            if (!AllowedProperties.Contains(property.Name))
                return Invalid($"Unknown property '{property.Name}'.");
        }

        if (!body.TryGetProperty("expectedVersion", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt32(out var expectedVersion) ||
            expectedVersion < 0)
            return Invalid("expectedVersion must be a non-negative integer.");

        if (!body.TryGetProperty("enabled", out var enabledElement) ||
            enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Invalid("enabled must be a boolean.");

        if (!body.TryGetProperty("requiresApproval", out var approvalElement) ||
            approvalElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Invalid("requiresApproval must be a boolean.");

        if (!TryReadRole(body, "minimumRequesterRole", out var requesterRole, out var requesterError))
            return Invalid(requesterError!);

        if (!TryReadRole(body, "minimumApproverRole", out var approverRole, out var approverError))
            return Invalid(approverError!);

        int? maxArgumentLength = null;
        if (body.TryGetProperty("maxArgumentLength", out var maxElement) &&
            maxElement.ValueKind != JsonValueKind.Null)
        {
            if (maxElement.ValueKind != JsonValueKind.Number || !maxElement.TryGetInt32(out var max))
                return Invalid("maxArgumentLength must be an integer or null.");
            if (max < 1)
                return Invalid("maxArgumentLength must be at least 1.");
            maxArgumentLength = max;
        }

        return new(
            (expectedVersion,
                new ToolPolicySettings(
                    enabledElement.GetBoolean(),
                    requesterRole,
                    approverRole,
                    approvalElement.GetBoolean(),
                    maxArgumentLength)),
            null);
    }

    private static bool TryReadRole(
        JsonElement body,
        string propertyName,
        out WorkspaceRole? role,
        out string? error)
    {
        role = null;
        error = null;

        if (!body.TryGetProperty(propertyName, out var element) ||
            element.ValueKind == JsonValueKind.Null)
            return true;

        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        role = text switch
        {
            "Member" => WorkspaceRole.Member,
            "Admin" => WorkspaceRole.Admin,
            "Owner" => WorkspaceRole.Owner,
            _ => null
        };

        if (role is null)
        {
            error = $"{propertyName} must be one of \"Member\", \"Admin\", \"Owner\" or null.";
            return false;
        }

        return true;
    }
}
