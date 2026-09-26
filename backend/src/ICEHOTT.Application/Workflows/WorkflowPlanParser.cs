using System.Text;
using System.Text.Json;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Workflows;

public sealed record WorkflowRetryPolicy(
    int MaxAttempts,
    int InitialDelaySeconds,
    int MaxDelaySeconds,
    double BackoffMultiplier)
{
    public TimeSpan DelayForAttempt(int completedAttempt)
    {
        var exponent = Math.Max(0, completedAttempt - 1);
        var seconds = InitialDelaySeconds * Math.Pow(BackoffMultiplier, exponent);
        return TimeSpan.FromSeconds(Math.Min(MaxDelaySeconds, seconds));
    }
}

public sealed record WorkflowPlanStep(
    string Key,
    WorkflowStepType Type,
    IReadOnlyList<string> DependsOn,
    string RawJson,
    string? ToolName,
    string? ArgumentsJson,
    int? DelaySeconds,
    WorkspaceRole? MinimumApproverRole,
    bool RequiresDifferentApprover,
    WorkflowRetryPolicy Retry);

public sealed record WorkflowPlan(IReadOnlyList<WorkflowPlanStep> Steps);

public sealed class WorkflowPlanException(string message) : Exception(message);

public static class WorkflowPlanParser
{
    public const int MaxDefinitionBytes = 64 * 1024;
    public const int MaxSteps = 100;
    public const int MaxDependenciesPerStep = 20;

    public static WorkflowPlan Parse(string definitionJson)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
            throw new WorkflowPlanException("Workflow definition JSON is required.");

        if (Encoding.UTF8.GetByteCount(definitionJson) > MaxDefinitionBytes)
            throw new WorkflowPlanException("Workflow definition exceeds the server size limit.");

        using var document = JsonDocument.Parse(
            definitionJson,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new WorkflowPlanException("Workflow definition must be a JSON object.");

        if (HasDuplicateProperties(document.RootElement))
            throw new WorkflowPlanException("Workflow definition contains duplicate JSON properties.");

        if (!document.RootElement.TryGetProperty("steps", out var stepsElement) ||
            stepsElement.ValueKind != JsonValueKind.Array)
            throw new WorkflowPlanException("Workflow definition requires a steps array.");

        if (stepsElement.GetArrayLength() > MaxSteps)
            throw new WorkflowPlanException($"Workflow definition may contain at most {MaxSteps} steps.");

        var steps = new List<WorkflowPlanStep>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in stepsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new WorkflowPlanException("Every workflow step must be an object.");

            var key = RequiredString(element, "key", 120);
            if (!keys.Add(key))
                throw new WorkflowPlanException($"Duplicate workflow step key '{key}'.");

            var typeText = RequiredString(element, "type", 32);
            if (!TryParseType(typeText, out var type))
                throw new WorkflowPlanException($"Unsupported workflow step type '{typeText}'.");

            var dependencies = ParseDependencies(element, key);
            var retry = ParseRetry(element);

            ValidateTypeSpecific(element, type, key);

            var toolName = type == WorkflowStepType.Tool
                ? RequiredString(element, "toolName", 160)
                : null;
            var argumentsJson = type == WorkflowStepType.Tool
                ? element.GetProperty("arguments").GetRawText()
                : null;
            int? delaySeconds = type == WorkflowStepType.Delay
                ? RequiredInt(element, "delaySeconds", 0, 86400, key)
                : null;
            WorkspaceRole? minimumApproverRole = type == WorkflowStepType.Checkpoint
                ? ReadApproverRole(element, key)
                : null;
            var requiresDifferentApprover = type != WorkflowStepType.Checkpoint ||
                                            ReadSeparationFlag(element, key);

            steps.Add(new WorkflowPlanStep(
                key,
                type,
                dependencies,
                element.GetRawText(),
                toolName,
                argumentsJson,
                delaySeconds,
                minimumApproverRole,
                requiresDifferentApprover,
                retry));
        }

        foreach (var step in steps)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!keys.Contains(dependency))
                    throw new WorkflowPlanException(
                        $"Workflow step '{step.Key}' depends on unknown step '{dependency}'.");
            }
        }

        if (steps.Count == 0)
            throw new WorkflowPlanException("Workflow definition must contain at least one step.");

        if (steps.Count(x => x.DependsOn.Count == 0) != 1)
            throw new WorkflowPlanException(
                "Workflow graph must have exactly one deterministic entry step.");

        ValidateAcyclic(steps);
        return new WorkflowPlan(steps);
    }

    private static IReadOnlyList<string> ParseDependencies(
        JsonElement step,
        string stepKey)
    {
        if (!step.TryGetProperty("dependsOn", out var element))
            return Array.Empty<string>();

        if (element.ValueKind != JsonValueKind.Array)
            throw new WorkflowPlanException(
                $"Workflow step '{stepKey}' dependsOn must be an array.");

        if (element.GetArrayLength() > MaxDependenciesPerStep)
            throw new WorkflowPlanException(
                $"Workflow step '{stepKey}' has too many dependencies.");

        var dependencies = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new WorkflowPlanException(
                    $"Workflow step '{stepKey}' dependencies must be strings.");

            var dependency = item.GetString()?.Trim() ?? string.Empty;
            if (dependency.Length is < 1 or > 120)
                throw new WorkflowPlanException(
                    $"Workflow step '{stepKey}' contains an invalid dependency.");

            if (string.Equals(dependency, stepKey, StringComparison.Ordinal))
                throw new WorkflowPlanException(
                    $"Workflow step '{stepKey}' cannot depend on itself.");

            if (!seen.Add(dependency))
                throw new WorkflowPlanException(
                    $"Workflow step '{stepKey}' contains duplicate dependency '{dependency}'.");

            dependencies.Add(dependency);
        }

        return dependencies;
    }
    private static WorkflowRetryPolicy ParseRetry(JsonElement step)
    {
        var policy = new WorkflowRetryPolicy(1, 1, 60, 2.0);
        if (!step.TryGetProperty("retry", out var retry))
            return policy;

        if (retry.ValueKind != JsonValueKind.Object)
            throw new WorkflowPlanException("Workflow retry policy must be an object.");

        var maxAttempts = OptionalInt(retry, "maxAttempts", 1, 10, 1);
        var initialDelay = OptionalInt(retry, "initialDelaySeconds", 1, 3600, 1);
        var maxDelay = OptionalInt(retry, "maxDelaySeconds", 1, 86400, 60);
        var multiplier = OptionalDouble(retry, "backoffMultiplier", 1.0, 10.0, 2.0);

        if (maxDelay < initialDelay)
            throw new WorkflowPlanException(
                "Workflow retry maxDelaySeconds must be at least initialDelaySeconds.");

        return new WorkflowRetryPolicy(
            maxAttempts,
            initialDelay,
            maxDelay,
            multiplier);
    }

    private static void ValidateTypeSpecific(
        JsonElement step,
        WorkflowStepType type,
        string key)
    {
        switch (type)
        {
            case WorkflowStepType.Delay:
                _ = RequiredInt(step, "delaySeconds", 0, 86400, key);
                break;

            case WorkflowStepType.Tool:
                _ = RequiredString(step, "toolName", 160);
                if (!step.TryGetProperty("arguments", out var arguments) ||
                    arguments.ValueKind != JsonValueKind.Object)
                    throw new WorkflowPlanException(
                        $"Tool step '{key}' requires an arguments object.");
                break;

            case WorkflowStepType.Checkpoint:
            case WorkflowStepType.Artifact:
            case WorkflowStepType.Condition:
                break;

            default:
                throw new WorkflowPlanException(
                    $"Unsupported workflow step type '{type}'.");
        }
    }

    private static void ValidateAcyclic(IReadOnlyList<WorkflowPlanStep> steps)
    {
        var map = steps.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var step in steps)
            Visit(step.Key, map, state);
    }

    private static void Visit(
        string key,
        IReadOnlyDictionary<string, WorkflowPlanStep> map,
        IDictionary<string, int> state)
    {
        if (state.TryGetValue(key, out var current))
        {
            if (current == 1)
                throw new WorkflowPlanException("Workflow dependency graph contains a cycle.");
            if (current == 2)
                return;
        }

        state[key] = 1;
        foreach (var dependency in map[key].DependsOn)
            Visit(dependency, map, state);
        state[key] = 2;
    }
    private static bool TryParseType(
        string value,
        out WorkflowStepType type)
    {
        type = value.Trim().ToLowerInvariant() switch
        {
            "tool" => WorkflowStepType.Tool,
            "checkpoint" => WorkflowStepType.Checkpoint,
            "artifact" => WorkflowStepType.Artifact,
            "delay" => WorkflowStepType.Delay,
            "condition" => WorkflowStepType.Condition,
            _ => 0
        };

        return type != 0;
    }

    private static string RequiredString(
        JsonElement element,
        string property,
        int maxLength)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
            throw new WorkflowPlanException(
                $"Workflow property '{property}' must be a string.");

        var result = value.GetString()?.Trim() ?? string.Empty;
        if (result.Length is < 1 || result.Length > maxLength ||
            result.Any(char.IsControl))
            throw new WorkflowPlanException(
                $"Workflow property '{property}' is invalid.");

        return result;
    }

    private static WorkspaceRole ReadApproverRole(
        JsonElement step,
        string stepKey)
    {
        if (!step.TryGetProperty("minimumApproverRole", out var roleElement))
            return WorkspaceRole.Admin;

        if (roleElement.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<WorkspaceRole>(
                roleElement.GetString(),
                true,
                out var role) ||
            role is < WorkspaceRole.Member or > WorkspaceRole.Owner)
        {
            throw new WorkflowPlanException(
                $"Checkpoint step '{stepKey}' has an invalid minimumApproverRole.");
        }

        return role;
    }

    private static bool ReadSeparationFlag(
        JsonElement step,
        string stepKey)
    {
        if (!step.TryGetProperty("requiresDifferentApprover", out var flag))
            return true;

        if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new WorkflowPlanException(
                $"Checkpoint step '{stepKey}' has an invalid requiresDifferentApprover flag.");

        return flag.GetBoolean();
    }

    private static int RequiredInt(
        JsonElement element,
        string property,
        int minimum,
        int maximum,
        string stepKey)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result) ||
            result < minimum ||
            result > maximum)
            throw new WorkflowPlanException(
                $"Workflow step '{stepKey}' has invalid {property}.");

        return result;
    }

    private static int OptionalInt(
        JsonElement element,
        string property,
        int minimum,
        int maximum,
        int fallback)
    {
        if (!element.TryGetProperty(property, out var value))
            return fallback;

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result) ||
            result < minimum ||
            result > maximum)
            throw new WorkflowPlanException(
                $"Workflow retry property '{property}' is invalid.");

        return result;
    }
    private static double OptionalDouble(
        JsonElement element,
        string property,
        double minimum,
        double maximum,
        double fallback)
    {
        if (!element.TryGetProperty(property, out var value))
            return fallback;

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var result) ||
            !double.IsFinite(result) ||
            result < minimum ||
            result > maximum)
            throw new WorkflowPlanException(
                $"Workflow retry property '{property}' is invalid.");

        return result;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name) ||
                        HasDuplicateProperties(property.Value))
                        return true;
                }
                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (HasDuplicateProperties(item))
                        return true;
                }
                return false;

            default:
                return false;
        }
    }
}
