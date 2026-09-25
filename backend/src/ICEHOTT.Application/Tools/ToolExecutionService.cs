using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Tools;

public sealed class ToolExecutionService(
    IWorkspaceRepository workspaces,
    IToolExecutionRepository executions,
    IToolPolicyRepository policies,
    IToolRegistry registry,
    TimeProvider clock)
{
    /// <summary>
    /// Upper bound on the canonical JSON persisted per execution. Per-tool
    /// schemas are far smaller; this is a backstop for future tools.
    /// </summary>
    public const int MaxArgumentsBytes = 16 * 1024;

    public async Task<ToolOperationResult<IReadOnlyList<ToolDefinitionView>>> ListToolsAsync(
        Guid userId,
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        var membership = await workspaces.FindMembershipAsync(
            userId,
            workspaceId,
            cancellationToken);

        if (membership is null)
            return new(null, "workspace_not_found");

        var overlays = await LoadOverlaysAsync(workspaceId, cancellationToken);

        // D9: only enabled tools the caller may request, with effective values.
        var items = registry.All
            .Select(tool => (
                tool.Definition,
                Policy: ToolPolicyEvaluator.Effective(
                    tool.Definition,
                    overlays.GetValueOrDefault(tool.Definition.Name))))
            .Where(x => x.Policy.Enabled &&
                        membership.Role >= x.Policy.MinimumRequesterRole)
            .Select(x => Map(x.Definition, x.Policy))
            .ToArray();

        return new(items, null);
    }

    public async Task<ToolOperationResult<ToolExecutionView>> RequestAsync(
        Guid userId,
        Guid workspaceId,
        string toolName,
        JsonElement arguments,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var membership = await workspaces.FindMembershipAsync(
            userId,
            workspaceId,
            cancellationToken);

        if (membership is null)
            return new(null, "workspace_not_found");

        var tool = registry.Find(toolName);
        if (tool is null)
            return new(null, "tool_not_found");

        // D7: the current effective policy decides admission.
        var overlay = await policies.FindAsync(
            workspaceId,
            tool.Definition.Name,
            cancellationToken);
        var policy = ToolPolicyEvaluator.Effective(tool.Definition, overlay);

        if (membership.Role < policy.MinimumRequesterRole)
            return new(null, "forbidden");

        if (!policy.Enabled)
            return new(null, "tool_disabled");

        if (!IsValidIdempotencyKey(idempotencyKey))
            return new(
                null,
                "invalid_idempotency_key",
                ["Idempotency key must be 8-128 visible characters without whitespace."]);

        if (arguments.ValueKind != JsonValueKind.Object)
            return new(
                null,
                "invalid_arguments",
                ["Tool arguments must be a JSON object."]);

        // JsonElement lookups resolve duplicate property names to the LAST
        // occurrence, while canonicalization persists ALL occurrences. Without
        // this check an earlier duplicate would be stored and hashed without
        // ever being validated (type/length/control-character bypass).
        if (HasDuplicateProperties(arguments))
            return new(
                null,
                "invalid_arguments",
                ["Tool arguments must not contain duplicate property names."]);

        var validation = tool.ValidateArguments(arguments);
        if (!validation.IsValid)
            return new(null, "invalid_arguments", validation.Errors);

        var limitErrors = ToolPolicyEvaluator.CheckArgumentLimits(
            tool.Definition,
            policy,
            arguments);
        if (limitErrors.Count > 0)
            return new(null, "invalid_arguments", limitErrors);

        var canonicalArguments = Canonicalize(arguments);
        if (Encoding.UTF8.GetByteCount(canonicalArguments) > MaxArgumentsBytes)
            return new(
                null,
                "invalid_arguments",
                [$"Tool arguments must be at most {MaxArgumentsBytes} bytes."]);

        var argumentsHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalArguments)))
            .ToLowerInvariant();

        var existing = await executions.FindByIdempotencyAsync(
            workspaceId,
            tool.Definition.Name,
            idempotencyKey.Trim(),
            cancellationToken);

        if (existing is not null)
            return await ReplayAsync(
                existing,
                argumentsHash,
                cancellationToken);

        var now = clock.GetUtcNow();
        var execution = new ToolExecution(
            Guid.NewGuid(),
            workspaceId,
            userId,
            tool.Definition.Name,
            tool.Definition.RiskLevel,
            canonicalArguments,
            argumentsHash,
            idempotencyKey.Trim(),
            now,
            policy);

        await executions.AddAsync(execution, cancellationToken);
        await executions.AddAuditEventAsync(
            Audit(
                execution,
                ToolExecutionAuditEventType.Requested,
                userId,
                now),
            cancellationToken);

        // D6: an execution that runs right away is decided under the policy
        // version just read; commit only if that version is still current.
        // (A PendingApproval row is re-evaluated at approval instead.)
        if (!policy.RequiresApproval)
            await policies.GuardUnchangedAsync(
                overlay,
                workspaceId,
                tool.Definition.Name,
                now,
                cancellationToken);

        var inserted = await executions.SaveChangesAsync(cancellationToken);
        if (inserted == ToolPersistenceOutcome.PolicyConflict)
            return new(null, "policy_changed");

        if (inserted == ToolPersistenceOutcome.DuplicateIdempotencyKey)
        {
            // Lost an insert race against a concurrent request with the same
            // key. The unique index guarantees only one row exists; return it
            // instead of surfacing a 500, and never run the handler here.
            var winner = await executions.FindByIdempotencyAsync(
                workspaceId,
                tool.Definition.Name,
                idempotencyKey.Trim(),
                cancellationToken);

            return winner is null
                ? new(null, "idempotency_conflict")
                : await ReplayAsync(winner, argumentsHash, cancellationToken);
        }

        if (inserted != ToolPersistenceOutcome.Saved)
            return new(null, "invalid_state");

        if (execution.Status == ToolExecutionStatus.PendingApproval)
            return new(
                await MapExecutionAsync(execution, cancellationToken),
                null);

        return await ExecuteReadyAsync(
            execution,
            tool,
            cancellationToken);
    }

    public async Task<ToolOperationResult<IReadOnlyList<ToolExecutionView>>> ListExecutionsAsync(
        Guid userId,
        Guid workspaceId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var membership = await workspaces.FindMembershipAsync(
            userId,
            workspaceId,
            cancellationToken);

        if (membership is null)
            return new(null, "workspace_not_found");

        var items = await executions.ListAsync(
            workspaceId,
            Math.Clamp(limit, 1, 100),
            cancellationToken);

        var overlays = await LoadOverlaysAsync(workspaceId, cancellationToken);
        var visibleItems = items
            .Where(item => CanViewExecution(
                membership.Role,
                userId,
                item,
                overlays.GetValueOrDefault(item.ToolName)))
            .ToArray();

        var views = new List<ToolExecutionView>(visibleItems.Length);
        foreach (var item in visibleItems)
            views.Add(await MapExecutionAsync(item, cancellationToken));

        return new(views, null);
    }

    public async Task<ToolOperationResult<ToolExecutionView>> GetAsync(
        Guid userId,
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        var membership = await workspaces.FindMembershipAsync(
            userId,
            workspaceId,
            cancellationToken);

        if (membership is null)
            return new(null, "workspace_not_found");

        var execution = await executions.FindAsync(
            workspaceId,
            executionId,
            cancellationToken);

        if (execution is null ||
            !CanViewExecution(
                membership.Role,
                userId,
                execution,
                await policies.FindAsync(workspaceId, execution.ToolName, cancellationToken)))
            return new(null, "execution_not_found");

        return new(
            await MapExecutionAsync(execution, cancellationToken),
            null);
    }

    public async Task<ToolOperationResult<ToolExecutionView>> ApproveAsync(
        Guid approverUserId,
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        var membership = await workspaces.FindMembershipAsync(
            approverUserId,
            workspaceId,
            cancellationToken);

        if (membership is null)
            return new(null, "workspace_not_found");

        var execution = await executions.FindAsync(
            workspaceId,
            executionId,
            cancellationToken);

        if (execution is null)
            return new(null, "execution_not_found");

        var tool = registry.Find(execution.ToolName);
        if (tool is null)
            return new(null, "tool_not_found");

        if (!execution.PolicyRequiresApproval)
            return new(null, "approval_not_required");

        // D5: re-read the current policy and apply the stricter of the
        // admission snapshot and the current policy. Loosening never helps a
        // pending execution; tightening always applies.
        var overlay = await policies.FindAsync(
            workspaceId,
            execution.ToolName,
            cancellationToken);
        var policy = execution.PolicySnapshot.StricterOf(
            ToolPolicyEvaluator.Effective(tool.Definition, overlay));

        if (membership.Role < (policy.MinimumApproverRole ?? WorkspaceRole.Admin))
            return new(null, "forbidden");

        if (execution.RequestedByUserId == approverUserId)
            return new(null, "self_approval_forbidden");

        if (execution.Status != ToolExecutionStatus.PendingApproval)
            return new(null, "invalid_state");

        if (!policy.Enabled)
            return new(null, "tool_disabled");

        // The handler runs with the requester's identity (for example the
        // audit note's CreatedByUserId). Authority is re-resolved from the
        // database at approval time: a requester who has since been removed
        // from the workspace or demoted below the (stricter) requester role
        // must not have the tool executed on their behalf.
        var requesterMembership = await workspaces.FindMembershipAsync(
            execution.RequestedByUserId,
            workspaceId,
            cancellationToken);

        if (requesterMembership is null ||
            requesterMembership.Role < policy.MinimumRequesterRole)
            return new(null, "requester_no_longer_authorized");

        using (var storedArguments = JsonDocument.Parse(execution.ArgumentsJson))
        {
            var limitErrors = ToolPolicyEvaluator.CheckArgumentLimits(
                tool.Definition,
                policy,
                storedArguments.RootElement);
            if (limitErrors.Count > 0)
                return new(null, "policy_limit_exceeded", limitErrors);
        }

        var now = clock.GetUtcNow();
        execution.Approve(approverUserId, now);
        await executions.AddAuditEventAsync(
            Audit(
                execution,
                ToolExecutionAuditEventType.Approved,
                approverUserId,
                now),
            cancellationToken);

        // D6: commit the approval only if the policy version evaluated above is
        // still current (a concurrent Owner change fails this commit closed).
        await policies.GuardUnchangedAsync(
            overlay,
            workspaceId,
            execution.ToolName,
            now,
            cancellationToken);

        // Guarded PendingApproval -> Ready transition. If a concurrent
        // approve/reject already moved the row, nothing is committed and the
        // handler is not run a second time.
        var approved = await executions.SaveChangesAsync(cancellationToken);
        if (approved == ToolPersistenceOutcome.PolicyConflict)
            return new(null, "policy_changed");
        if (approved != ToolPersistenceOutcome.Saved)
            return new(null, "invalid_state");

        return await ExecuteReadyAsync(
            execution,
            tool,
            cancellationToken);
    }

    public async Task<ToolOperationResult<ToolExecutionView>> RejectAsync(
        Guid approverUserId,
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        var membership = await workspaces.FindMembershipAsync(
            approverUserId,
            workspaceId,
            cancellationToken);

        if (membership is null)
            return new(null, "workspace_not_found");

        var execution = await executions.FindAsync(
            workspaceId,
            executionId,
            cancellationToken);

        if (execution is null)
            return new(null, "execution_not_found");

        var tool = registry.Find(execution.ToolName);
        if (tool is null)
            return new(null, "tool_not_found");

        if (!execution.PolicyRequiresApproval)
            return new(null, "approval_not_required");

        // D8: rejecting needs the stricter approver role but stays possible
        // for a disabled tool, so stale pending executions can be cleaned up.
        var policy = execution.PolicySnapshot.StricterOf(
            ToolPolicyEvaluator.Effective(
                tool.Definition,
                await policies.FindAsync(workspaceId, execution.ToolName, cancellationToken)));

        if (membership.Role < (policy.MinimumApproverRole ?? WorkspaceRole.Admin))
            return new(null, "forbidden");

        if (execution.RequestedByUserId == approverUserId)
            return new(null, "self_approval_forbidden");

        if (execution.Status != ToolExecutionStatus.PendingApproval)
            return new(null, "invalid_state");

        var now = clock.GetUtcNow();
        execution.Reject(approverUserId, now);
        await executions.AddAuditEventAsync(
            Audit(
                execution,
                ToolExecutionAuditEventType.Rejected,
                approverUserId,
                now),
            cancellationToken);

        if (await executions.SaveChangesAsync(cancellationToken) !=
            ToolPersistenceOutcome.Saved)
            return new(null, "invalid_state");

        return new(
            await MapExecutionAsync(execution, cancellationToken),
            null);
    }

    private async Task<ToolOperationResult<ToolExecutionView>> ExecuteReadyAsync(
        ToolExecution execution,
        IWorkspaceTool tool,
        CancellationToken cancellationToken)
    {
        var startedAt = clock.GetUtcNow();
        execution.Start(startedAt);
        await executions.AddAuditEventAsync(
            Audit(
                execution,
                ToolExecutionAuditEventType.Started,
                execution.RequestedByUserId,
                startedAt),
            cancellationToken);

        // Guarded Ready -> Running claim: exactly one request can win it, so
        // the handler below runs at most once per execution.
        if (await executions.SaveChangesAsync(cancellationToken) !=
            ToolPersistenceOutcome.Saved)
            return new(null, "invalid_state");

        ToolExecutionOutput output;
        try
        {
            using var document = JsonDocument.Parse(
                execution.ArgumentsJson);
            output = await tool.ExecuteAsync(
                new ToolExecutionContext(
                    execution.WorkspaceId,
                    execution.RequestedByUserId,
                    execution.Id,
                    startedAt),
                document.RootElement.Clone(),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            executions.DiscardPendingSideEffects(execution);
            var cancelledAt = clock.GetUtcNow();
            execution.Fail(
                "tool_cancelled",
                "Tool execution was cancelled.",
                cancelledAt);
            await executions.AddAuditEventAsync(
                Audit(
                    execution,
                    ToolExecutionAuditEventType.Failed,
                    execution.RequestedByUserId,
                    cancelledAt),
                CancellationToken.None);
            await executions.SaveChangesAsync(
                CancellationToken.None);
            throw;
        }
        catch
        {
            // A handler may have staged writes before throwing. They must not
            // be committed together with the Failed status: a Failed execution
            // has no side effects. Exception details are never persisted.
            executions.DiscardPendingSideEffects(execution);
            var failedAt = clock.GetUtcNow();
            execution.Fail(
                "tool_execution_failed",
                "Tool execution failed.",
                failedAt);
            await executions.AddAuditEventAsync(
                Audit(
                    execution,
                    ToolExecutionAuditEventType.Failed,
                    execution.RequestedByUserId,
                    failedAt),
                cancellationToken);
            await executions.SaveChangesAsync(cancellationToken);

            return new(
                await MapExecutionAsync(
                    execution,
                    cancellationToken),
                "tool_execution_failed");
        }

        var completedAt = clock.GetUtcNow();
        execution.Succeed(
            output.ResultJson,
            completedAt);
        await executions.AddAuditEventAsync(
            Audit(
                execution,
                ToolExecutionAuditEventType.Succeeded,
                execution.RequestedByUserId,
                completedAt),
            cancellationToken);

        // Handler side effects, the Succeeded status and the audit event
        // commit atomically in one SaveChanges, guarded on Status = Running.
        if (await executions.SaveChangesAsync(cancellationToken) !=
            ToolPersistenceOutcome.Saved)
            return new(null, "invalid_state");

        return new(
            await MapExecutionAsync(
                execution,
                cancellationToken),
            null);
    }

    private async Task<ToolOperationResult<ToolExecutionView>> ReplayAsync(
        ToolExecution existing,
        string argumentsHash,
        CancellationToken cancellationToken)
    {
        // Same key + different arguments is a conflict; stored arguments of
        // an existing execution are never replaced.
        if (!string.Equals(
                existing.ArgumentsHash,
                argumentsHash,
                StringComparison.Ordinal))
            return new(null, "idempotency_conflict");

        return new(
            await MapExecutionAsync(existing, cancellationToken),
            null);
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

    private async Task<ToolExecutionView> MapExecutionAsync(
        ToolExecution execution,
        CancellationToken cancellationToken)
    {
        var events = await executions.ListAuditEventsAsync(
            execution.WorkspaceId,
            execution.Id,
            cancellationToken);

        using var arguments = JsonDocument.Parse(execution.ArgumentsJson);
        JsonElement? result = null;
        if (!string.IsNullOrWhiteSpace(execution.ResultJson))
        {
            using var resultDocument = JsonDocument.Parse(execution.ResultJson);
            result = resultDocument.RootElement.Clone();
        }

        return new ToolExecutionView(
            execution.Id,
            execution.WorkspaceId,
            execution.RequestedByUserId,
            execution.ToolName,
            execution.RiskLevel,
            execution.Status,
            execution.IdempotencyKey,
            arguments.RootElement.Clone(),
            execution.ApprovedByUserId,
            execution.RequestedAtUtc,
            execution.ApprovedAtUtc,
            execution.StartedAtUtc,
            execution.CompletedAtUtc,
            result,
            execution.ErrorCode,
            execution.ErrorMessage,
            events.Select(x => new ToolAuditEventView(
                x.EventType,
                x.ActorUserId,
                x.OccurredAtUtc)).ToArray(),
            execution.PolicyVersion);
    }

    // D9: non-requesters need max(snapshot requester role, current effective
    // requester role) to see an execution.
    private bool CanViewExecution(
        WorkspaceRole callerRole,
        Guid callerUserId,
        ToolExecution execution,
        ToolPolicy? overlay)
    {
        if (execution.RequestedByUserId == callerUserId)
            return true;

        var tool = registry.Find(execution.ToolName);
        if (tool is null)
            return false;

        var required = execution.PolicySnapshot
            .StricterOf(ToolPolicyEvaluator.Effective(tool.Definition, overlay))
            .MinimumRequesterRole;

        return callerRole >= required;
    }

    private async Task<Dictionary<string, ToolPolicy>> LoadOverlaysAsync(
        Guid workspaceId,
        CancellationToken cancellationToken) =>
        (await policies.ListAsync(workspaceId, cancellationToken))
            .ToDictionary(x => x.ToolName, StringComparer.Ordinal);

    private static ToolDefinitionView Map(
        ToolDefinition definition,
        EffectiveToolPolicy policy) =>
        new(
            definition.Name,
            definition.Description,
            definition.RiskLevel,
            policy.MinimumRequesterRole,
            policy.RequiresApproval,
            policy.MinimumApproverRole,
            definition.Arguments
                .Select(argument =>
                    argument.Type == ToolArgumentType.String &&
                    policy.MaxArgumentLength is { } limit &&
                    (argument.MaxLength is null || limit < argument.MaxLength)
                        ? argument with { MaxLength = limit }
                        : argument)
                .ToArray(),
            policy.Version);

    private static ToolExecutionAuditEvent Audit(
        ToolExecution execution,
        ToolExecutionAuditEventType eventType,
        Guid? actorUserId,
        DateTimeOffset occurredAtUtc) =>
        new(
            Guid.NewGuid(),
            execution.Id,
            execution.WorkspaceId,
            eventType,
            actorUserId,
            occurredAtUtc);

    private static bool IsValidIdempotencyKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is >= 8 and <= 128 &&
        value.All(character =>
            !char.IsWhiteSpace(character) &&
            !char.IsControl(character));

    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, element);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
