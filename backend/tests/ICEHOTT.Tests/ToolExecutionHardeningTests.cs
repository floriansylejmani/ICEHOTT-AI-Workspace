using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Tools;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ICEHOTT.Tests.ToolSecurityFixture;

namespace ICEHOTT.Tests;

/// <summary>
/// Phase 4 independent security review: service-level tests for failure
/// isolation and race conditions that cannot be driven deterministically
/// through HTTP.
/// </summary>
public sealed class ToolExecutionHardeningTests(ToolSecurityFixture fx)
    : IClassFixture<ToolSecurityFixture>
{
    private const string Note = "workspace.audit-note.create";

    [Fact]
    public async Task Failed_Handler_Is_Recorded_Safely_And_Its_Staged_Side_Effects_Are_Discarded()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        var calls = new CallCounter();
        var key = NewKey("fail");

        Guid executionId;
        using (var scope = fx.Factory.Services.CreateScope())
        {
            var tool = new StagesNoteThenThrowsTool(
                scope.ServiceProvider.GetRequiredService<IWorkspaceAuditNoteRepository>(),
                calls);
            var service = CreateService(scope, tool);

            var result = await service.RequestAsync(
                fx.Owner.UserId, workspaceId, tool.Definition.Name, Args(new { text = "boom" }), key);

            Assert.Equal("tool_execution_failed", result.ErrorCode);
            Assert.NotNull(result.Value);
            Assert.Equal(ToolExecutionStatus.Failed, result.Value.Status);
            Assert.Equal("tool_execution_failed", result.Value.ErrorCode);
            Assert.Equal("Tool execution failed.", result.Value.ErrorMessage);
            Assert.Null(result.Value.Result);
            executionId = result.Value.Id;
        }

        var row = await fx.FindExecutionAsync(executionId);
        Assert.Equal(ToolExecutionStatus.Failed, row!.Status);
        Assert.NotNull(row.CompletedAtUtc);
        Assert.Null(row.ResultJson);
        // Exception text (which here contains a fake secret) is never persisted.
        Assert.DoesNotContain("hunter2", row.ErrorMessage);
        Assert.DoesNotContain("Password", row.ErrorMessage);

        // Regression: the note staged by the handler before it threw used to be
        // committed by the SaveChanges that recorded the Failed status.
        Assert.Equal(0, await fx.CountNotesAsync(executionId));
        Assert.Equal(
            [ToolExecutionAuditEventType.Requested, ToolExecutionAuditEventType.Started, ToolExecutionAuditEventType.Failed],
            await fx.AuditTrailAsync(executionId));

        // Failed is terminal: replaying the key returns it and never re-runs the handler.
        using (var scope = fx.Factory.Services.CreateScope())
        {
            var tool = new StagesNoteThenThrowsTool(
                scope.ServiceProvider.GetRequiredService<IWorkspaceAuditNoteRepository>(),
                calls);
            var replay = await CreateService(scope, tool).RequestAsync(
                fx.Owner.UserId, workspaceId, tool.Definition.Name, Args(new { text = "boom" }), key);

            Assert.Null(replay.ErrorCode);
            Assert.Equal(executionId, replay.Value!.Id);
            Assert.Equal(ToolExecutionStatus.Failed, replay.Value.Status);
        }

        Assert.Equal(1, calls.Count);
    }

    [Fact]
    public async Task Stale_Concurrent_Approval_Cannot_Run_Sensitive_Handler_Twice()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner,
            (fx.Admin, WorkspaceRole.Admin),
            (fx.Admin2, WorkspaceRole.Admin));
        var executionId = await RequestPendingNoteAsync(workspaceId, "race");

        // Racer B reads the execution while it is still PendingApproval...
        using var scopeB = fx.Factory.Services.CreateScope();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        var staleB = await dbB.ToolExecutions.SingleAsync(x => x.Id == executionId);
        Assert.Equal(ToolExecutionStatus.PendingApproval, staleB.Status);

        // ...racer A approves and runs the tool...
        using (var scopeA = fx.Factory.Services.CreateScope())
        {
            var winner = await scopeA.ServiceProvider.GetRequiredService<ToolExecutionService>()
                .ApproveAsync(fx.Owner.UserId, workspaceId, executionId);
            Assert.Null(winner.ErrorCode);
            Assert.Equal(ToolExecutionStatus.Succeeded, winner.Value!.Status);
        }

        // ...then B acts on its stale PendingApproval snapshot.
        var serviceB = scopeB.ServiceProvider.GetRequiredService<ToolExecutionService>();
        var loserApprove = await serviceB.ApproveAsync(fx.Admin2.UserId, workspaceId, executionId);
        Assert.Equal("invalid_state", loserApprove.ErrorCode);

        var row = await fx.FindExecutionAsync(executionId);
        Assert.Equal(ToolExecutionStatus.Succeeded, row!.Status);
        Assert.Equal(fx.Owner.UserId, row.ApprovedByUserId);
        Assert.Equal(1, await fx.CountNotesAsync(executionId));

        var trail = await fx.AuditTrailAsync(executionId);
        Assert.Equal(1, trail.Count(x => x == ToolExecutionAuditEventType.Approved));
        Assert.Equal(1, trail.Count(x => x == ToolExecutionAuditEventType.Started));
        Assert.Equal(1, trail.Count(x => x == ToolExecutionAuditEventType.Succeeded));
    }

    [Fact]
    public async Task Stale_Concurrent_Reject_Cannot_Override_An_Approval()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner,
            (fx.Admin, WorkspaceRole.Admin),
            (fx.Admin2, WorkspaceRole.Admin));
        var executionId = await RequestPendingNoteAsync(workspaceId, "race reject");

        using var scopeB = fx.Factory.Services.CreateScope();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        _ = await dbB.ToolExecutions.SingleAsync(x => x.Id == executionId);

        using (var scopeA = fx.Factory.Services.CreateScope())
        {
            var approved = await scopeA.ServiceProvider.GetRequiredService<ToolExecutionService>()
                .ApproveAsync(fx.Owner.UserId, workspaceId, executionId);
            Assert.Null(approved.ErrorCode);
        }

        var loserReject = await scopeB.ServiceProvider.GetRequiredService<ToolExecutionService>()
            .RejectAsync(fx.Admin2.UserId, workspaceId, executionId);
        Assert.Equal("invalid_state", loserReject.ErrorCode);

        var row = await fx.FindExecutionAsync(executionId);
        Assert.Equal(ToolExecutionStatus.Succeeded, row!.Status);
        Assert.Equal(fx.Owner.UserId, row.ApprovedByUserId);
        Assert.DoesNotContain(ToolExecutionAuditEventType.Rejected, await fx.AuditTrailAsync(executionId));
    }

    [Fact]
    public async Task Concurrent_Duplicate_Insert_Returns_Existing_Execution_Without_Rerunning()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        var calls = new CallCounter();
        var key = NewKey("insert-race");

        Guid firstId;
        using (var scope = fx.Factory.Services.CreateScope())
        {
            var first = await CreateService(scope, new CountingTool(calls))
                .RequestAsync(fx.Owner.UserId, workspaceId, CountingTool.Name, Args(new { text = "one" }), key);
            Assert.Null(first.ErrorCode);
            firstId = first.Value!.Id;
        }

        // Simulate the losing side of a race: the pre-insert idempotency lookup
        // misses the row a concurrent request just committed, so the insert
        // hits the unique (WorkspaceId, ToolName, IdempotencyKey) index.
        using (var scope = fx.Factory.Services.CreateScope())
        {
            var repository = new MissFirstLookupRepository(
                scope.ServiceProvider.GetRequiredService<IToolExecutionRepository>());
            var service = new ToolExecutionService(
                scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>(),
                repository,
                scope.ServiceProvider.GetRequiredService<IToolPolicyRepository>(),
                new ToolRegistry([new CountingTool(calls)]),
                TimeProvider.System);

            var second = await service.RequestAsync(
                fx.Owner.UserId, workspaceId, CountingTool.Name, Args(new { text = "one" }), key);

            Assert.Null(second.ErrorCode);
            Assert.Equal(firstId, second.Value!.Id);
            Assert.Equal(ToolExecutionStatus.Succeeded, second.Value.Status);
        }

        using (var scope = fx.Factory.Services.CreateScope())
        {
            var repository = new MissFirstLookupRepository(
                scope.ServiceProvider.GetRequiredService<IToolExecutionRepository>());
            var service = new ToolExecutionService(
                scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>(),
                repository,
                scope.ServiceProvider.GetRequiredService<IToolPolicyRepository>(),
                new ToolRegistry([new CountingTool(calls)]),
                TimeProvider.System);

            var conflicting = await service.RequestAsync(
                fx.Owner.UserId, workspaceId, CountingTool.Name, Args(new { text = "two" }), key);
            Assert.Equal("idempotency_conflict", conflicting.ErrorCode);
        }

        Assert.Equal(1, calls.Count);
        Assert.Equal(1, await fx.CountExecutionsAsync(workspaceId));
    }

    [Fact]
    public async Task Duplicate_Property_Names_Are_Rejected_Before_Tool_Validation()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var scope = fx.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ToolExecutionService>();

        using var document = JsonDocument.Parse("""{"text":"x","text":"y"}""");
        var result = await service.RequestAsync(
            fx.Owner.UserId, workspaceId, "workspace.echo", document.RootElement.Clone(), NewKey());

        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));
    }

    private async Task<Guid> RequestPendingNoteAsync(Guid workspaceId, string message)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ToolExecutionService>()
            .RequestAsync(fx.Admin.UserId, workspaceId, Note, Args(new { message }), NewKey("pending"));
        Assert.Null(result.ErrorCode);
        Assert.Equal(ToolExecutionStatus.PendingApproval, result.Value!.Status);
        return result.Value.Id;
    }

    private static ToolExecutionService CreateService(IServiceScope scope, IWorkspaceTool tool) =>
        new(
            scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>(),
            scope.ServiceProvider.GetRequiredService<IToolExecutionRepository>(),
            scope.ServiceProvider.GetRequiredService<IToolPolicyRepository>(),
            new ToolRegistry([tool]),
            TimeProvider.System);

    private static JsonElement Args(object value) =>
        JsonSerializer.SerializeToElement(value);

    private sealed class CallCounter
    {
        private int _count;
        public int Count => _count;
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private static ToolDefinition ReadOnlyDefinition(string name) =>
        new(
            name,
            "test tool",
            ToolRiskLevel.ReadOnly,
            WorkspaceRole.Member,
            RequiresApproval: false,
            MinimumApproverRole: null,
            [new ToolArgumentDefinition("text", ToolArgumentType.String, Required: true, MaxLength: 50)]);

    private static ToolArgumentValidationResult ValidateText(JsonElement arguments) =>
        arguments.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? ToolArgumentValidationResult.Valid
            : ToolArgumentValidationResult.Invalid("text required");

    /// <summary>Stages a real workspace audit note, then throws.</summary>
    private sealed class StagesNoteThenThrowsTool(
        IWorkspaceAuditNoteRepository notes,
        CallCounter calls) : IWorkspaceTool
    {
        public ToolDefinition Definition { get; } = ReadOnlyDefinition("test.stages-then-throws");

        public ToolArgumentValidationResult ValidateArguments(JsonElement arguments) =>
            ValidateText(arguments);

        public async Task<ToolExecutionOutput> ExecuteAsync(
            ToolExecutionContext context,
            JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            calls.Increment();
            await notes.AddAsync(
                new WorkspaceAuditNote(
                    Guid.NewGuid(),
                    context.WorkspaceId,
                    context.ExecutionId,
                    context.RequestedByUserId,
                    "partial side effect",
                    DateTimeOffset.UtcNow),
                cancellationToken);

            throw new InvalidOperationException(
                "Downstream failure: Host=db;Username=icehott;Password=hunter2");
        }
    }

    private sealed class CountingTool(CallCounter calls) : IWorkspaceTool
    {
        public const string Name = "test.counting";

        public ToolDefinition Definition { get; } = ReadOnlyDefinition(Name);

        public ToolArgumentValidationResult ValidateArguments(JsonElement arguments) =>
            ValidateText(arguments);

        public Task<ToolExecutionOutput> ExecuteAsync(
            ToolExecutionContext context,
            JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            calls.Increment();
            return Task.FromResult(new ToolExecutionOutput("""{"ok":true}"""));
        }
    }

    /// <summary>
    /// Hides an existing row from the first idempotency lookup only, which is
    /// exactly what a request that loses an insert race observes.
    /// </summary>
    private sealed class MissFirstLookupRepository(IToolExecutionRepository inner)
        : IToolExecutionRepository
    {
        private bool _missed;

        public Task<ToolExecution?> FindByIdempotencyAsync(
            Guid workspaceId, string toolName, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            if (!_missed)
            {
                _missed = true;
                return Task.FromResult<ToolExecution?>(null);
            }

            return inner.FindByIdempotencyAsync(workspaceId, toolName, idempotencyKey, cancellationToken);
        }

        public Task<ToolExecution?> FindAsync(Guid workspaceId, Guid executionId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(workspaceId, executionId, cancellationToken);

        public Task<IReadOnlyList<ToolExecution>> ListAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default) =>
            inner.ListAsync(workspaceId, limit, cancellationToken);

        public Task<IReadOnlyList<ToolExecutionAuditEvent>> ListAuditEventsAsync(Guid workspaceId, Guid executionId, CancellationToken cancellationToken = default) =>
            inner.ListAuditEventsAsync(workspaceId, executionId, cancellationToken);

        public Task AddAsync(ToolExecution execution, CancellationToken cancellationToken = default) =>
            inner.AddAsync(execution, cancellationToken);

        public Task AddAuditEventAsync(ToolExecutionAuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            inner.AddAuditEventAsync(auditEvent, cancellationToken);

        public Task<ToolPersistenceOutcome> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            inner.SaveChangesAsync(cancellationToken);

        public void DiscardPendingSideEffects(ToolExecution execution) =>
            inner.DiscardPendingSideEffects(execution);
    }
}
