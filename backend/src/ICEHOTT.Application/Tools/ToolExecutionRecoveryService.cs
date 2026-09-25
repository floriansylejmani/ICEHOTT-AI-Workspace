using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Tools;

namespace ICEHOTT.Application.Tools;

/// <summary>
/// Reconciles abandoned Running rows without invoking a tool handler.
/// An expired lease proves only that the worker stopped renewing/finishing;
/// it does not prove that an external side effect did not happen.
/// </summary>
public sealed class ToolExecutionRecoveryService(
    IToolExecutionRepository executions)
{
    public async Task<int> RecoverExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var due = await executions.ListExpiredRunningAsync(
            now, 100, cancellationToken);
        var recovered = 0;

        foreach (var execution in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            execution.MarkOutcomeUnknown(now);
            await executions.AddAuditEventAsync(
                new ToolExecutionAuditEvent(
                    Guid.NewGuid(),
                    execution.Id,
                    execution.WorkspaceId,
                    ToolExecutionAuditEventType.OutcomeUnknown,
                    null,
                    now),
                cancellationToken);

            // Status is a concurrency token: a concurrently completed
            // execution wins, while this transition and its audit event are
            // discarded together. Nothing is ever executed or retried here.
            if (await executions.SaveChangesAsync(cancellationToken) ==
                ToolPersistenceOutcome.Saved)
                recovered++;
        }

        return recovered;
    }
}
