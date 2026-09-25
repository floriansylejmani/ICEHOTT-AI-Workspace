using ICEHOTT.Domain.Tools;

namespace ICEHOTT.Application.Abstractions;

/// <summary>
/// Result of persisting a tool-execution state change.
/// </summary>
public enum ToolPersistenceOutcome
{
    /// <summary>All pending changes were committed.</summary>
    Saved = 1,

    /// <summary>
    /// The execution row changed status underneath this request (for example
    /// two approvers racing). Nothing was committed and pending changes were
    /// discarded.
    /// </summary>
    ConcurrencyConflict = 2,

    /// <summary>
    /// A concurrent request already inserted an execution with the same
    /// (workspace, tool, idempotency key). Nothing was committed and pending
    /// changes were discarded.
    /// </summary>
    DuplicateIdempotencyKey = 3,

    /// <summary>
    /// The tool policy row changed (or was first created) after it was read
    /// for this decision. Nothing was committed and pending changes were
    /// discarded; the caller must re-evaluate under the current policy.
    /// </summary>
    PolicyConflict = 4
}

public interface IToolExecutionRepository
{
    Task<ToolExecution?> FindAsync(
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default);

    Task<ToolExecution?> FindByIdempotencyAsync(
        Guid workspaceId,
        string toolName,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ToolExecution>> ListAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ToolExecution>> ListExpiredRunningAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ToolExecutionAuditEvent>> ListAuditEventsAsync(
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        ToolExecution execution,
        CancellationToken cancellationToken = default);

    Task AddAuditEventAsync(
        ToolExecutionAuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits pending changes. Execution status is an optimistic concurrency
    /// token, so a status transition only commits if the row still holds the
    /// status that was read. Concurrency and idempotency-key races are reported
    /// as outcomes instead of exceptions, and in those cases nothing is committed.
    /// </summary>
    Task<ToolPersistenceOutcome> SaveChangesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops every uncommitted change except those on <paramref name="execution"/>.
    /// Called when a tool handler throws, so partial side effects staged by the
    /// handler are never committed together with the Failed status.
    /// </summary>
    void DiscardPendingSideEffects(ToolExecution execution);
}
