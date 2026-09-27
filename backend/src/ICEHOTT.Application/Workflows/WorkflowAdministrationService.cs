using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Workflows;

public sealed record WorkflowDefinitionMutationView(
    Guid Id, string Name, string? Description,
    WorkflowDefinitionStatus Status, WorkspaceRole MinimumRunRole,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record WorkflowVersionMutationView(
    Guid Id, Guid WorkflowDefinitionId, int VersionNumber,
    string DefinitionHash, WorkflowVersionStatus Status,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? RetiredAtUtc);

public sealed record WorkflowRunMutationView(
    Guid Id, Guid WorkflowDefinitionId, Guid WorkflowVersionId,
    WorkflowRunStatus Status, string? CurrentStepKey,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? CancellationRequestedAtUtc);

public sealed record WorkflowAdministrationResult<T>(
    T? Value, string? ErrorCode, bool IsReplay = false)
{
    public bool Succeeded => ErrorCode is null;
}

public sealed class WorkflowAdministrationService(
    IWorkflowAdministrationStore store,
    TimeProvider clock)
{
    public async Task<WorkflowAdministrationResult<WorkflowDefinitionMutationView>> CreateDefinitionAsync(
        Guid actorUserId, Guid workspaceId, string name, string? description,
        WorkspaceRole minimumRunRole, CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? string.Empty;
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (name.Length is < 1 or > 120)
            return new(null, "invalid_workflow_name");
        if (description?.Length > 1000)
            return new(null, "invalid_workflow_description");
        if (minimumRunRole is < WorkspaceRole.Member or > WorkspaceRole.Owner)
            return new(null, "invalid_minimum_run_role");

        var result = await store.CreateDefinitionAsync(
            actorUserId, workspaceId, name, description, minimumRunRole,
            clock.GetUtcNow(), cancellationToken);
        return Map(result, x => Map(x.Definition!));
    }

    public async Task<WorkflowAdministrationResult<WorkflowVersionMutationView>> CreateVersionAsync(
        Guid actorUserId, Guid workspaceId, Guid workflowId,
        string definitionJson, CancellationToken cancellationToken = default)
    {
        string canonical;
        try
        {
            _ = WorkflowPlanParser.Parse(definitionJson);
            canonical = Canonicalize(definitionJson);
        }
        catch (JsonException)
        {
            return new(null, "invalid_workflow_definition");
        }
        catch (WorkflowPlanException)
        {
            return new(null, "invalid_workflow_definition");
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var result = await store.CreateVersionAsync(
            actorUserId, workspaceId, workflowId, canonical, hash,
            clock.GetUtcNow(), cancellationToken);
        return Map(result, x => Map(x.Version!));
    }

    public async Task<WorkflowAdministrationResult<WorkflowVersionMutationView>> ActivateVersionAsync(
        Guid actorUserId, Guid workspaceId, Guid workflowId, Guid versionId,
        CancellationToken cancellationToken = default)
    {
        var result = await store.ActivateVersionAsync(
            actorUserId, workspaceId, workflowId, versionId,
            clock.GetUtcNow(), cancellationToken);
        return Map(result, x => Map(x.Version!));
    }

    public async Task<WorkflowAdministrationResult<WorkflowDefinitionMutationView>> ArchiveDefinitionAsync(
        Guid actorUserId, Guid workspaceId, Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var result = await store.ArchiveDefinitionAsync(
            actorUserId, workspaceId, workflowId, clock.GetUtcNow(),
            cancellationToken);
        return Map(result, x => Map(x.Definition!));
    }

    public async Task<WorkflowAdministrationResult<WorkflowRunMutationView>> CreateRunAsync(
        Guid actorUserId, Guid workspaceId, Guid workflowId,
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        idempotencyKey = idempotencyKey?.Trim() ?? string.Empty;
        if (idempotencyKey.Length is < 1 or > 128 ||
            idempotencyKey.Any(char.IsControl))
            return new(null, "invalid_idempotency_key");

        var result = await store.CreateRunAsync(
            actorUserId, workspaceId, workflowId, idempotencyKey,
            clock.GetUtcNow(), cancellationToken);
        return Map(result, x => Map(x.Run!));
    }

    public async Task<WorkflowAdministrationResult<WorkflowRunMutationView>> CancelRunAsync(
        Guid actorUserId, Guid workspaceId, Guid runId,
        CancellationToken cancellationToken = default)
    {
        var result = await store.CancelRunAsync(
            actorUserId, workspaceId, runId, clock.GetUtcNow(),
            cancellationToken);
        return Map(result, x => Map(x.Run!));
    }

    public async Task<WorkflowAdministrationResult<WorkflowRunMutationView>> RetryRunAsync(
        Guid actorUserId, Guid workspaceId, Guid runId,
        CancellationToken cancellationToken = default)
    {
        var result = await store.RetryRunAsync(
            actorUserId, workspaceId, runId, cancellationToken);
        return Map(result, x => Map(x.Run!));
    }

    private static WorkflowAdministrationResult<T> Map<T>(
        WorkflowAdministrationPersistenceResult result,
        Func<WorkflowAdministrationPersistenceResult, T> projector)
    {
        if (result.Outcome is WorkflowAdministrationPersistenceOutcome.Saved or
            WorkflowAdministrationPersistenceOutcome.IdempotentReplay)
            return new(projector(result), null,
                result.Outcome == WorkflowAdministrationPersistenceOutcome.IdempotentReplay);

        return new(default, result.Outcome switch
        {
            WorkflowAdministrationPersistenceOutcome.WorkspaceNotFound => "workspace_not_found",
            WorkflowAdministrationPersistenceOutcome.Forbidden => "forbidden",
            WorkflowAdministrationPersistenceOutcome.WorkflowNotFound => "workflow_not_found",
            WorkflowAdministrationPersistenceOutcome.WorkflowVersionNotFound => "workflow_version_not_found",
            WorkflowAdministrationPersistenceOutcome.RunNotFound => "workflow_run_not_found",
            WorkflowAdministrationPersistenceOutcome.RunRoleNotAuthorized => "workflow_run_not_authorized",
            WorkflowAdministrationPersistenceOutcome.InvalidState => "invalid_state",
            WorkflowAdministrationPersistenceOutcome.ConcurrencyConflict => "concurrency_conflict",
            WorkflowAdministrationPersistenceOutcome.RetryNotSupported => "run_retry_not_supported",
            _ => "workflow_operation_failed"
        });
    }

    private static WorkflowDefinitionMutationView Map(WorkflowDefinition value) =>
        new(value.Id, value.Name, value.Description, value.Status,
            value.MinimumRunRole, value.CreatedAtUtc, value.UpdatedAtUtc);

    private static WorkflowVersionMutationView Map(WorkflowVersion value) =>
        new(value.Id, value.WorkflowDefinitionId, value.VersionNumber,
            value.DefinitionHash, value.Status, value.CreatedAtUtc,
            value.ActivatedAtUtc, value.RetiredAtUtc);

    private static WorkflowRunMutationView Map(WorkflowRun value) =>
        new(value.Id, value.WorkflowDefinitionId, value.WorkflowVersionId,
            value.Status, value.CurrentStepKey, value.CreatedAtUtc,
            value.CompletedAtUtc, value.CancellationRequestedAtUtc);

    private static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject()
                         .OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
                WriteCanonical(writer, item);
            writer.WriteEndArray();
            return;
        }

        element.WriteTo(writer);
    }
}
