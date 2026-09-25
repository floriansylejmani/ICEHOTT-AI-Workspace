using System.Text.Json;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Tools;

/// <summary>
/// Pure policy rules for Phase 4.5 packet B. The built-in registry definition is
/// the floor; an overlay can only tighten it (docs/PHASE-4.5-B-TOOL-POLICY.md D1/D2).
/// </summary>
public static class ToolPolicyEvaluator
{
    /// <summary>Largest MaxLength among the tool's bounded string arguments.</summary>
    public static int? BuiltInMaxArgumentLength(ToolDefinition definition) =>
        definition.Arguments
            .Where(x => x.Type == ToolArgumentType.String && x.MaxLength is not null)
            .Select(x => x.MaxLength)
            .DefaultIfEmpty(null)
            .Max();

    public static EffectiveToolPolicy Effective(
        ToolDefinition definition,
        ToolPolicy? overlay)
    {
        var requester = Max(definition.MinimumRequesterRole, overlay?.MinimumRequesterRole);
        var requiresApproval = definition.RequiresApproval || overlay?.RequiresApproval == true;

        WorkspaceRole? approver = null;
        if (requiresApproval)
        {
            // D2: never below Admin, never below whoever may request the tool.
            var floor = Max(WorkspaceRole.Admin, definition.MinimumApproverRole);
            floor = Max(floor, overlay?.MinimumApproverRole);
            approver = Max(floor, requester);
        }

        return new EffectiveToolPolicy(
            overlay?.Version ?? 0,
            overlay?.Enabled ?? true,
            requester,
            approver,
            requiresApproval,
            overlay?.MaxArgumentLength);
    }

    /// <summary>
    /// Checks that <paramref name="settings"/> only tightens the built-in
    /// definition. Returns null when valid, else (error code, messages).
    /// </summary>
    public static (string Code, string[] Errors)? Validate(
        ToolDefinition definition,
        ToolPolicySettings settings)
    {
        if (settings.MinimumRequesterRole is { } requester &&
            requester < definition.MinimumRequesterRole)
            return ("policy_downgrade_rejected",
                [$"minimumRequesterRole cannot be below the built-in {definition.MinimumRequesterRole}."]);

        var approverFloor = Max(WorkspaceRole.Admin, definition.MinimumApproverRole);
        if (settings.MinimumApproverRole is { } approver && approver < approverFloor)
            return ("policy_downgrade_rejected",
                [$"minimumApproverRole cannot be below {approverFloor}."]);

        if (definition.RequiresApproval && !settings.RequiresApproval)
            return ("policy_downgrade_rejected",
                ["requiresApproval cannot be removed from a tool that requires approval."]);

        if (settings.MaxArgumentLength is { } max)
        {
            var builtIn = BuiltInMaxArgumentLength(definition);
            if (builtIn is null)
                return ("invalid_policy", ["This tool has no bounded string arguments; maxArgumentLength must be null."]);
            if (max < 1 || max > builtIn)
                return ("invalid_policy", [$"maxArgumentLength must be between 1 and {builtIn}."]);
        }

        return null;
    }

    /// <summary>
    /// Enforces the effective max length on every bounded string argument.
    /// Returns the violation messages (empty when within limits).
    /// </summary>
    public static IReadOnlyList<string> CheckArgumentLimits(
        ToolDefinition definition,
        EffectiveToolPolicy policy,
        JsonElement arguments)
    {
        var errors = new List<string>();
        if (arguments.ValueKind != JsonValueKind.Object)
            return errors;

        foreach (var argument in definition.Arguments)
        {
            if (argument.Type != ToolArgumentType.String)
                continue;

            var limit = MinNullable(argument.MaxLength, policy.MaxArgumentLength);
            if (limit is null ||
                !arguments.TryGetProperty(argument.Name, out var value) ||
                value.ValueKind != JsonValueKind.String)
                continue;

            if ((value.GetString() ?? string.Empty).Length > limit)
                errors.Add($"Argument '{argument.Name}' must be at most {limit} characters.");
        }

        return errors;
    }

    private static WorkspaceRole Max(WorkspaceRole a, WorkspaceRole? b) =>
        b is { } value && value > a ? value : a;

    private static int? MinNullable(int? a, int? b) =>
        a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}
