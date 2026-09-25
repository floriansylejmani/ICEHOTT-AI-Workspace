using ICEHOTT.Domain.Tools;

namespace ICEHOTT.Application.Abstractions;

public interface IToolPolicyRepository
{
    /// <summary>Tracked lookup; null means built-in defaults (version 0).</summary>
    Task<ToolPolicy?> FindAsync(
        Guid workspaceId,
        string toolName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ToolPolicy>> ListAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ToolPolicyAuditEvent>> ListAuditEventsAsync(
        Guid workspaceId,
        string toolName,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        ToolPolicy policy,
        CancellationToken cancellationToken = default);

    Task AddAuditEventAsync(
        ToolPolicyAuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a guard that makes the next SaveChanges commit only if the policy
    /// row still holds the version that was <paramref name="observed"/>:
    /// an existing row is re-asserted with <c>WHERE "Version" = observed</c>;
    /// when no row was observed, a version-0 default row is inserted, which
    /// collides with a concurrent first policy insert. A violated guard surfaces
    /// as <see cref="ToolPersistenceOutcome.PolicyConflict"/>.
    /// </summary>
    Task GuardUnchangedAsync(
        ToolPolicy? observed,
        Guid workspaceId,
        string toolName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<ToolPersistenceOutcome> SaveChangesAsync(
        CancellationToken cancellationToken = default);
}
