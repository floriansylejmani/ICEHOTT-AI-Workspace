using ICEHOTT.Domain.Workflows;

namespace ICEHOTT.Application.Abstractions;

public interface IWorkflowRepository
{
    Task AddDefinitionAsync(
        WorkflowDefinition definition,
        CancellationToken cancellationToken = default);

    Task<WorkflowDefinition?> FindDefinitionAsync(
        Guid workspaceId,
        Guid definitionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowDefinition>> ListDefinitionsAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default);

    Task AddVersionAsync(
        WorkflowVersion version,
        CancellationToken cancellationToken = default);
    Task<WorkflowVersion?> FindVersionAsync(
        Guid workspaceId,
        Guid definitionId,
        Guid versionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowVersion>> ListVersionsAsync(
        Guid workspaceId,
        Guid definitionId,
        CancellationToken cancellationToken = default);

    Task AddRunAsync(
        WorkflowRun run,
        CancellationToken cancellationToken = default);

    Task<WorkflowRun?> FindRunAsync(
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default);
    Task AddStepRunAsync(
        WorkflowStepRun stepRun,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowStepRun>> ListStepRunsAsync(
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken = default);

    Task AddCheckpointAsync(
        WorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<WorkflowCheckpoint?> FindCheckpointAsync(
        Guid workspaceId,
        Guid checkpointId,
        CancellationToken cancellationToken = default);

    Task AddTriggerAsync(
        WorkflowTrigger trigger,
        CancellationToken cancellationToken = default);
    Task<WorkflowTrigger?> FindTriggerAsync(
        Guid workspaceId,
        Guid triggerId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowTrigger>> ListTriggersAsync(
        Guid workspaceId,
        Guid definitionId,
        CancellationToken cancellationToken = default);

    Task AddTriggerFireAsync(
        WorkflowTriggerFire fire,
        CancellationToken cancellationToken = default);

    Task<WorkflowTriggerFire?> FindTriggerFireAsync(
        Guid workspaceId,
        Guid triggerId,
        DateTimeOffset scheduledForUtc,
        CancellationToken cancellationToken = default);
}
