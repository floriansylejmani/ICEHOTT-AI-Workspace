using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Abstractions;

public enum WorkflowAdministrationPersistenceOutcome
{
    Saved = 1,
    IdempotentReplay = 2,
    WorkspaceNotFound = 3,
    Forbidden = 4,
    WorkflowNotFound = 5,
    WorkflowVersionNotFound = 6,
    RunNotFound = 7,
    RunRoleNotAuthorized = 8,
    InvalidState = 9,
    ConcurrencyConflict = 10,
    RetryNotSupported = 11
}

public sealed record WorkflowAdministrationPersistenceResult(
    WorkflowAdministrationPersistenceOutcome Outcome,
    WorkflowDefinition? Definition = null,
    WorkflowVersion? Version = null,
    WorkflowRun? Run = null);

public interface IWorkflowAdministrationStore
{
    Task<WorkflowAdministrationPersistenceResult> CreateDefinitionAsync(
        Guid actorUserId, Guid workspaceId, string name, string? description,
        WorkspaceRole minimumRunRole, DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<WorkflowAdministrationPersistenceResult> CreateVersionAsync(
        Guid actorUserId, Guid workspaceId, Guid workflowId,
        string definitionJson, string definitionHash, DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<WorkflowAdministrationPersistenceResult> ActivateVersionAsync(
        Guid actorUserId, Guid workspaceId, Guid workflowId, Guid versionId,
        DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<WorkflowAdministrationPersistenceResult> ArchiveDefinitionAsync(
        Guid actorUserId, Guid workspaceId, Guid workflowId, DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<WorkflowAdministrationPersistenceResult> CreateRunAsync(
        Guid actorUserId, Guid workspaceId, Guid workflowId,
        string idempotencyKey, DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<WorkflowAdministrationPersistenceResult> CancelRunAsync(
        Guid actorUserId, Guid workspaceId, Guid runId, DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<WorkflowAdministrationPersistenceResult> RetryRunAsync(
        Guid actorUserId, Guid workspaceId, Guid runId,
        CancellationToken cancellationToken = default);
}
