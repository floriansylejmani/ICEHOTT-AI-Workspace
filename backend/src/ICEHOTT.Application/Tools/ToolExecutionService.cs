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
    IToolRegistry registry,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
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

        var items = registry.All
            .Where(tool => membership.Role >= tool.Definition.MinimumRequesterRole)
            .Select(tool => Map(tool.Definition))
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

        if (membership.Role < tool.Definition.MinimumRequesterRole)
            return new(null, "forbidden");

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

        var validation = tool.ValidateArguments(arguments);
        if (!validation.IsValid)
            return new(null, "invalid_arguments", validation.Errors);

        var canonicalArguments = Canonicalize(arguments);
        var argumentsHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalArguments)))
            .ToLowerInvariant();

        var existing = await executions.FindByIdempotencyAsync(
            workspaceId,
            tool.Definition.Name,
            idempotencyKey.Trim(),
            cancellationToken);

        if (existing is not null)
        {
            if (!string.Equals(
                    existing.ArgumentsHash,
                    argumentsHash,
                    StringComparison.Ordinal))
                return new(null, "idempotency_conflict");

            return new(
                await MapExecutionAsync(existing, cancellationToken),
                null);
        }

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
            tool.Definition.RequiresApproval,
            now);

        await executions.AddAsync(execution, cancellationToken);
        await executions.AddAuditEventAsync(
            Audit(
                execution,
                ToolExecutionAuditEventType.Requested,
                userId,
                now),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

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
        if (await workspaces.FindMembershipAsync(
                userId,
                workspaceId,
                cancellationToken) is null)
            return new(null, "workspace_not_found");

        var items = await executions.ListAsync(
            workspaceId,
            Math.Clamp(limit, 1, 100),
            cancellationToken);

        var views = new List<ToolExecutionView>(items.Count);
        foreach (var item in items)
            views.Add(await MapExecutionAsync(item, cancellationToken));

        return new(views, null);
    }

    public async Task<ToolOperationResult<ToolExecutionView>> GetAsync(
        Guid userId,
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(
                userId,
                workspaceId,
                cancellationToken) is null)
            return new(null, "workspace_not_found");

        var execution = await executions.FindAsync(
            workspaceId,
            executionId,
            cancellationToken);

        return execution is null
            ? new(null, "execution_not_found")
            : new(
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

        if (!tool.Definition.RequiresApproval)
            return new(null, "approval_not_required");

        var minimumApproverRole =
            tool.Definition.MinimumApproverRole ?? WorkspaceRole.Admin;

        if (membership.Role < minimumApproverRole)
            return new(null, "forbidden");

        if (execution.RequestedByUserId == approverUserId)
            return new(null, "self_approval_forbidden");

        if (execution.Status != ToolExecutionStatus.PendingApproval)
            return new(null, "invalid_state");

        var now = clock.GetUtcNow();
        execution.Approve(approverUserId, now);
        await executions.AddAuditEventAsync(
            Audit(
                execution,
                ToolExecutionAuditEventType.Approved,
                approverUserId,
                now),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

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

        if (!tool.Definition.RequiresApproval)
            return new(null, "approval_not_required");

        var minimumApproverRole =
            tool.Definition.MinimumApproverRole ?? WorkspaceRole.Admin;

        if (membership.Role < minimumApproverRole)
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
        await unitOfWork.SaveChangesAsync(cancellationToken);

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
        await unitOfWork.SaveChangesAsync(cancellationToken);

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
            await unitOfWork.SaveChangesAsync(
                CancellationToken.None);
            throw;
        }
        catch
        {
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
            await unitOfWork.SaveChangesAsync(cancellationToken);

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
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new(
            await MapExecutionAsync(
                execution,
                cancellationToken),
            null);
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
                x.OccurredAtUtc)).ToArray());
    }

    private static ToolDefinitionView Map(ToolDefinition definition) =>
        new(
            definition.Name,
            definition.Description,
            definition.RiskLevel,
            definition.MinimumRequesterRole,
            definition.RequiresApproval,
            definition.MinimumApproverRole,
            definition.Arguments);

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
