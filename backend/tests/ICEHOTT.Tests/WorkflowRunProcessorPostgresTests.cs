using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Tools;
using ICEHOTT.Application.Workflows;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using ICEHOTT.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ICEHOTT.Tests;

public sealed class WorkflowRunProcessorPostgresTests
{
    private static string? Connection =>
        Environment.GetEnvironmentVariable("ICEHOTT_POSTGRES_TEST_CONNECTION");

    [Fact]
    public async Task Delay_Workflow_Survives_Wait_And_Restart()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var clock = new MutableTimeProvider(
                new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var seed = await SeedWorkflowAsync(
                isolatedConnection,
                """
                {
                  "steps": [
                    { "key": "pause", "type": "delay", "delaySeconds": 10 }
                  ]
                }
                """,
                clock.GetUtcNow());

            var invoker = new DatabaseToolInvoker(
                isolatedConnection,
                clock);

            await using (var db = CreateContext(isolatedConnection))
            {
                var queue = new WorkflowRunQueue(db, clock);
                var lease = Assert.IsType<WorkflowRunLease>(
                    await queue.LeaseNextAsync(
                        Guid.NewGuid(),
                        TimeSpan.FromSeconds(90)));

                var processor = CreateProcessor(
                    db,
                    queue,
                    invoker,
                    clock);
                var result = await processor.ProcessAsync(lease);

                Assert.Equal(
                    WorkflowRunProcessDisposition.Waiting,
                    result.Disposition);
                Assert.Equal(
                    WorkflowRunStatus.Waiting,
                    result.Status);
            }

            clock.Advance(TimeSpan.FromSeconds(11));

            await using (var db = CreateContext(isolatedConnection))
            {
                var queue = new WorkflowRunQueue(db, clock);
                var lease = Assert.IsType<WorkflowRunLease>(
                    await queue.LeaseNextAsync(
                        Guid.NewGuid(),
                        TimeSpan.FromSeconds(90)));

                var processor = CreateProcessor(
                    db,
                    queue,
                    invoker,
                    clock);
                var result = await processor.ProcessAsync(lease);

                Assert.Equal(
                    WorkflowRunProcessDisposition.Completed,
                    result.Disposition);
                Assert.Equal(
                    WorkflowRunStatus.Succeeded,
                    result.Status);
            }

            await using var verify = CreateContext(isolatedConnection);
            var run = await verify.WorkflowRuns.SingleAsync(
                x => x.Id == seed.RunId);
            var step = await verify.WorkflowStepRuns.SingleAsync(
                x => x.WorkflowRunId == seed.RunId);

            Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);
            Assert.Equal(WorkflowStepRunStatus.Succeeded, step.Status);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task Tool_Intent_Uses_Stable_Idempotency_Key_After_Crash()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var clock = new MutableTimeProvider(
                new DateTimeOffset(2026, 9, 26, 13, 0, 0, TimeSpan.Zero));
            var seed = await SeedWorkflowAsync(
                isolatedConnection,
                ToolWorkflowJson(maxAttempts: 1),
                clock.GetUtcNow());

            var crashing = new DatabaseToolInvoker(
                isolatedConnection,
                clock,
                FakeToolOutcome.ThrowBeforePersist);

            Guid firstStepId;

            await using (var db = CreateContext(isolatedConnection))
            {
                var queue = new WorkflowRunQueue(db, clock);
                var lease = Assert.IsType<WorkflowRunLease>(
                    await queue.LeaseNextAsync(
                        Guid.NewGuid(),
                        TimeSpan.FromSeconds(30)));

                var processor = CreateProcessor(
                    db,
                    queue,
                    crashing,
                    clock);

                await Assert.ThrowsAsync<OperationCanceledException>(
                    () => processor.ProcessAsync(lease));

                firstStepId = await db.WorkflowStepRuns
                    .Where(x => x.WorkflowRunId == seed.RunId)
                    .Select(x => x.Id)
                    .SingleAsync();
            }

            Assert.Single(crashing.IdempotencyKeys);
            await ExpireLeaseAsync(
                isolatedConnection,
                seed.RunId,
                clock.GetUtcNow().AddSeconds(-1));

            var completing = new DatabaseToolInvoker(
                isolatedConnection,
                clock,
                FakeToolOutcome.Succeeded);

            await using (var db = CreateContext(isolatedConnection))
            {
                var queue = new WorkflowRunQueue(db, clock);
                var lease = Assert.IsType<WorkflowRunLease>(
                    await queue.LeaseNextAsync(
                        Guid.NewGuid(),
                        TimeSpan.FromSeconds(30)));
                var processor = CreateProcessor(
                    db,
                    queue,
                    completing,
                    clock);

                var result = await processor.ProcessAsync(lease);
                Assert.Equal(WorkflowRunStatus.Succeeded, result.Status);
            }

            Assert.Single(completing.IdempotencyKeys);
            Assert.Equal(
                crashing.IdempotencyKeys[0],
                completing.IdempotencyKeys[0]);

            await using var verify = CreateContext(isolatedConnection);
            var steps = await verify.WorkflowStepRuns
                .Where(x => x.WorkflowRunId == seed.RunId)
                .ToListAsync();

            Assert.Single(steps);
            Assert.Equal(firstStepId, steps[0].Id);
            Assert.Equal(WorkflowStepRunStatus.Succeeded, steps[0].Status);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task ReadOnly_Tool_Failure_Retries_With_New_Attempt()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var clock = new MutableTimeProvider(
                new DateTimeOffset(2026, 9, 26, 14, 0, 0, TimeSpan.Zero));
            var seed = await SeedWorkflowAsync(
                isolatedConnection,
                ToolWorkflowJson(maxAttempts: 2),
                clock.GetUtcNow());

            var invoker = new DatabaseToolInvoker(
                isolatedConnection,
                clock,
                FakeToolOutcome.Failed,
                FakeToolOutcome.Succeeded);

            await using (var db = CreateContext(isolatedConnection))
            {
                var queue = new WorkflowRunQueue(db, clock);
                var lease = Assert.IsType<WorkflowRunLease>(
                    await queue.LeaseNextAsync(
                        Guid.NewGuid(),
                        TimeSpan.FromSeconds(90)));
                var processor = CreateProcessor(
                    db,
                    queue,
                    invoker,
                    clock);

                var result = await processor.ProcessAsync(lease);
                Assert.Equal(
                    WorkflowRunProcessDisposition.Waiting,
                    result.Disposition);
                Assert.Equal(WorkflowRunStatus.Waiting, result.Status);
            }

            clock.Advance(TimeSpan.FromSeconds(2));

            await using (var db = CreateContext(isolatedConnection))
            {
                var queue = new WorkflowRunQueue(db, clock);
                var lease = Assert.IsType<WorkflowRunLease>(
                    await queue.LeaseNextAsync(
                        Guid.NewGuid(),
                        TimeSpan.FromSeconds(90)));
                var processor = CreateProcessor(
                    db,
                    queue,
                    invoker,
                    clock);

                var result = await processor.ProcessAsync(lease);
                Assert.Equal(WorkflowRunStatus.Succeeded, result.Status);
            }

            await using var verify = CreateContext(isolatedConnection);
            var attempts = await verify.WorkflowStepRuns
                .Where(x => x.WorkflowRunId == seed.RunId)
                .OrderBy(x => x.Attempt)
                .ToListAsync();

            Assert.Equal(2, attempts.Count);
            Assert.Equal(WorkflowStepRunStatus.Failed, attempts[0].Status);
            Assert.Equal(WorkflowStepRunStatus.Succeeded, attempts[1].Status);
            Assert.Equal(2, invoker.IdempotencyKeys.Count);
            Assert.NotEqual(
                invoker.IdempotencyKeys[0],
                invoker.IdempotencyKeys[1]);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task RunAs_Membership_Removal_Fails_Closed_Before_Step_Start()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var clock = new MutableTimeProvider(
                new DateTimeOffset(2026, 9, 26, 15, 0, 0, TimeSpan.Zero));
            var seed = await SeedWorkflowAsync(
                isolatedConnection,
                """
                {
                  "steps": [
                    { "key": "pause", "type": "delay", "delaySeconds": 1 }
                  ]
                }
                """,
                clock.GetUtcNow());

            WorkflowRunLease lease;
            await using (var claimDb = CreateContext(isolatedConnection))
            {
                var queue = new WorkflowRunQueue(claimDb, clock);
                lease = Assert.IsType<WorkflowRunLease>(
                    await queue.LeaseNextAsync(
                        Guid.NewGuid(),
                        TimeSpan.FromSeconds(90)));
            }

            await using (var revoke = CreateContext(isolatedConnection))
            {
                await revoke.WorkspaceMemberships
                    .Where(x =>
                        x.WorkspaceId == seed.WorkspaceId &&
                        x.UserId == seed.OwnerId)
                    .ExecuteDeleteAsync();
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                var queue = new WorkflowRunQueue(db, clock);
                var processor = CreateProcessor(
                    db,
                    queue,
                    new DatabaseToolInvoker(isolatedConnection, clock),
                    clock);

                var result = await processor.ProcessAsync(lease);

                Assert.Equal(WorkflowRunStatus.Failed, result.Status);
                Assert.Equal(
                    "workflow_run_as_not_authorized",
                    result.ErrorCode);
            }

            await using var verify = CreateContext(isolatedConnection);
            Assert.Empty(await verify.WorkflowStepRuns
                .Where(x => x.WorkflowRunId == seed.RunId)
                .ToListAsync());
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task Cancellation_Of_Waiting_Checkpoint_Is_Durable()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var clock = new MutableTimeProvider(
                new DateTimeOffset(2026, 9, 26, 16, 0, 0, TimeSpan.Zero));
            var seed = await SeedWorkflowAsync(
                isolatedConnection,
                """
                {
                  "steps": [
                    {
                      "key": "approve",
                      "type": "checkpoint",
                      "minimumApproverRole": "Admin",
                      "requiresDifferentApprover": true
                    }
                  ]
                }
                """,
                clock.GetUtcNow());

            await using (var db = CreateContext(isolatedConnection))
            {
                var queue = new WorkflowRunQueue(db, clock);
                var lease = Assert.IsType<WorkflowRunLease>(
                    await queue.LeaseNextAsync(
                        Guid.NewGuid(),
                        TimeSpan.FromSeconds(90)));
                var processor = CreateProcessor(
                    db,
                    queue,
                    new DatabaseToolInvoker(isolatedConnection, clock),
                    clock);

                var result = await processor.ProcessAsync(lease);
                Assert.Equal(WorkflowRunStatus.Waiting, result.Status);
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                var queue = new WorkflowRunQueue(db, clock);
                Assert.True(await queue.RequestCancellationAsync(
                    seed.WorkspaceId,
                    seed.RunId,
                    seed.OwnerId,
                    clock.GetUtcNow()));

                var lease = Assert.IsType<WorkflowRunLease>(
                    await queue.LeaseNextAsync(
                        Guid.NewGuid(),
                        TimeSpan.FromSeconds(90)));
                var processor = CreateProcessor(
                    db,
                    queue,
                    new DatabaseToolInvoker(isolatedConnection, clock),
                    clock);

                var result = await processor.ProcessAsync(lease);
                Assert.Equal(WorkflowRunStatus.Cancelled, result.Status);
            }

            await using var verify = CreateContext(isolatedConnection);
            var run = await verify.WorkflowRuns.SingleAsync(
                x => x.Id == seed.RunId);
            var step = await verify.WorkflowStepRuns.SingleAsync(
                x => x.WorkflowRunId == seed.RunId);

            Assert.Equal(WorkflowRunStatus.Cancelled, run.Status);
            Assert.Equal(WorkflowStepRunStatus.Cancelled, step.Status);
            Assert.NotNull(run.CancellationRequestedAtUtc);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task Sensitive_OutcomeUnknown_Stops_Workflow_Without_Retry()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var clock = new MutableTimeProvider(
                new DateTimeOffset(2026, 9, 26, 17, 0, 0, TimeSpan.Zero));
            var seed = await SeedWorkflowAsync(
                isolatedConnection,
                ToolWorkflowJson(maxAttempts: 3),
                clock.GetUtcNow());

            var invoker = new DatabaseToolInvoker(
                isolatedConnection,
                clock,
                FakeToolOutcome.OutcomeUnknown);

            await using (var db = CreateContext(isolatedConnection))
            {
                var queue = new WorkflowRunQueue(db, clock);
                var lease = Assert.IsType<WorkflowRunLease>(
                    await queue.LeaseNextAsync(
                        Guid.NewGuid(),
                        TimeSpan.FromSeconds(90)));
                var processor = CreateProcessor(
                    db,
                    queue,
                    invoker,
                    clock);

                var result = await processor.ProcessAsync(lease);

                Assert.Equal(
                    WorkflowRunStatus.OutcomeUnknown,
                    result.Status);
                Assert.Equal(
                    WorkflowRunProcessDisposition.Completed,
                    result.Disposition);
            }

            await using var verify = CreateContext(isolatedConnection);
            var steps = await verify.WorkflowStepRuns
                .Where(x => x.WorkflowRunId == seed.RunId)
                .ToListAsync();

            Assert.Single(steps);
            Assert.Equal(
                WorkflowStepRunStatus.OutcomeUnknown,
                steps[0].Status);
            Assert.Single(invoker.IdempotencyKeys);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    private static WorkflowRunProcessor CreateProcessor(
        ICEHOTTDbContext db,
        IWorkflowRunQueue queue,
        IWorkflowToolInvoker toolInvoker,
        TimeProvider clock) =>
        new(
            new WorkflowRepository(db),
            queue,
            new WorkflowAuditRepository(db),
            new WorkspaceRepository(db),
            toolInvoker,
            clock);

    private sealed record Seed(
        Guid OwnerId,
        Guid WorkspaceId,
        Guid DefinitionId,
        Guid VersionId,
        Guid RunId);

    private static async Task<Seed> SeedWorkflowAsync(
        string connectionString,
        string definitionJson,
        DateTimeOffset now)
    {
        await using var db = CreateContext(connectionString);
        await db.Database.MigrateAsync();

        var ownerId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.Users.Add(new User(
            ownerId,
            $"{ownerId:N}@processor.test",
            "Processor Owner",
            "not-a-real-hash",
            now));
        db.Workspaces.Add(new Workspace(
            workspaceId,
            "Processor",
            $"processor-{workspaceId:N}",
            ownerId,
            now));
        db.WorkspaceMemberships.Add(new WorkspaceMembership(
            workspaceId,
            ownerId,
            WorkspaceRole.Owner,
            now));

        db.WorkflowDefinitions.Add(new WorkflowDefinition(
            definitionId,
            workspaceId,
            "Processor Workflow",
            null,
            WorkspaceRole.Member,
            ownerId,
            now));

        var version = new WorkflowVersion(
            versionId,
            definitionId,
            workspaceId,
            1,
            definitionJson,
            new string('a', 64),
            ownerId,
            now);
        version.Activate(now);
        db.WorkflowVersions.Add(version);

        db.WorkflowRuns.Add(new WorkflowRun(
            runId,
            workspaceId,
            definitionId,
            versionId,
            ownerId,
            ownerId,
            $"processor-{runId:N}",
            now));

        await db.SaveChangesAsync();

        return new Seed(
            ownerId,
            workspaceId,
            definitionId,
            versionId,
            runId);
    }

    private static string ToolWorkflowJson(int maxAttempts) =>
        $$"""
        {
          "steps": [
            {
              "key": "tool",
              "type": "tool",
              "toolName": "workspace.echo",
              "arguments": { "text": "hello" },
              "retry": {
                "maxAttempts": {{maxAttempts}},
                "initialDelaySeconds": 1,
                "maxDelaySeconds": 10,
                "backoffMultiplier": 2
              }
            }
          ]
        }
        """;
    private enum FakeToolOutcome
    {
        Succeeded = 1,
        Failed = 2,
        OutcomeUnknown = 3,
        ThrowBeforePersist = 4
    }

    private sealed class DatabaseToolInvoker(
        string connectionString,
        TimeProvider clock,
        params FakeToolOutcome[] outcomes) : IWorkflowToolInvoker
    {
        private readonly Queue<FakeToolOutcome> _outcomes =
            new(outcomes.Length == 0
                ? [FakeToolOutcome.Succeeded]
                : outcomes);

        public List<string> IdempotencyKeys { get; } = [];

        public async Task<ToolOperationResult<ToolExecutionView>> RequestAsync(
            Guid userId,
            Guid workspaceId,
            string toolName,
            JsonElement arguments,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            IdempotencyKeys.Add(idempotencyKey);

            var outcome = _outcomes.Count > 1
                ? _outcomes.Dequeue()
                : _outcomes.Peek();

            if (outcome == FakeToolOutcome.ThrowBeforePersist)
                throw new OperationCanceledException("Simulated worker crash.");

            await using var db = CreateContext(connectionString);
            var existing = await db.ToolExecutions.SingleOrDefaultAsync(
                x =>
                    x.WorkspaceId == workspaceId &&
                    x.ToolName == toolName &&
                    x.IdempotencyKey == idempotencyKey,
                cancellationToken);

            if (existing is not null)
                return new(Map(existing), null);

            var now = clock.GetUtcNow();
            var risk = outcome == FakeToolOutcome.OutcomeUnknown
                ? ToolRiskLevel.SensitiveWrite
                : ToolRiskLevel.ReadOnly;

            var execution = new ToolExecution(
                Guid.NewGuid(),
                workspaceId,
                userId,
                toolName,
                risk,
                arguments.GetRawText(),
                new string('b', 64),
                idempotencyKey,
                requiresApproval: false,
                now);

            execution.Start(
                now,
                Guid.NewGuid(),
                now.AddSeconds(30),
                now.AddSeconds(45));

            switch (outcome)
            {
                case FakeToolOutcome.Succeeded:
                    execution.Succeed(
                        "{\"ok\":true}",
                        now.AddMilliseconds(1));
                    break;

                case FakeToolOutcome.Failed:
                    execution.Fail(
                        "tool_execution_failed",
                        "Tool execution failed.",
                        now.AddMilliseconds(1));
                    break;

                case FakeToolOutcome.OutcomeUnknown:
                    execution.MarkOutcomeUnknown(
                        now.AddMilliseconds(1));
                    break;
            }

            db.ToolExecutions.Add(execution);
            await db.SaveChangesAsync(cancellationToken);

            return new(Map(execution), null);
        }

        public async Task<ToolOperationResult<ToolExecutionView>> GetAsync(
            Guid userId,
            Guid workspaceId,
            Guid executionId,
            CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext(connectionString);
            var execution = await db.ToolExecutions.SingleOrDefaultAsync(
                x => x.WorkspaceId == workspaceId && x.Id == executionId,
                cancellationToken);

            return execution is null
                ? new(null, "execution_not_found")
                : new(Map(execution), null);
        }

        public async Task<ToolOperationResult<ToolExecutionView>> CancelAsync(
            Guid userId,
            Guid workspaceId,
            Guid executionId,
            CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext(connectionString);
            var execution = await db.ToolExecutions.SingleOrDefaultAsync(
                x => x.WorkspaceId == workspaceId && x.Id == executionId,
                cancellationToken);

            if (execution is null)
                return new(null, "execution_not_found");

            if (execution.Status is
                ToolExecutionStatus.PendingApproval or
                ToolExecutionStatus.Ready)
            {
                execution.Cancel(clock.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
            }

            return new(Map(execution), null);
        }

        private static ToolExecutionView Map(ToolExecution execution) =>
            new(
                execution.Id,
                execution.WorkspaceId,
                execution.RequestedByUserId,
                execution.ToolName,
                execution.RiskLevel,
                execution.Status,
                execution.IdempotencyKey,
                ParseJson(execution.ArgumentsJson),
                execution.ApprovedByUserId,
                execution.RequestedAtUtc,
                execution.ApprovedAtUtc,
                execution.StartedAtUtc,
                execution.CompletedAtUtc,
                string.IsNullOrWhiteSpace(execution.ResultJson)
                    ? null
                    : ParseJson(execution.ResultJson),
                execution.ErrorCode,
                execution.ErrorMessage,
                Array.Empty<ToolAuditEventView>());

        private static JsonElement ParseJson(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
    private sealed class MutableTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));

            _now = _now.Add(duration);
        }
    }

    private static async Task ExpireLeaseAsync(
        string connectionString,
        Guid runId,
        DateTimeOffset expiredAtUtc)
    {
        await using var db = CreateContext(connectionString);
        await db.WorkflowRuns
            .Where(x => x.Id == runId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    x => x.LeaseExpiresAtUtc,
                    expiredAtUtc));
    }

    private static async Task<(string DatabaseName, string ConnectionString)>
        CreateIsolatedDatabaseAsync()
    {
        var source = new NpgsqlConnectionStringBuilder(Connection!);
        var databaseName = $"icehott_processor_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(Connection!)
        {
            Database = "postgres",
            Pooling = false
        };

        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"CREATE DATABASE \"{databaseName}\"",
            connection);
        await command.ExecuteNonQueryAsync();

        source.Database = databaseName;
        source.Pooling = false;
        return (databaseName, source.ConnectionString);
    }

    private static async Task DropIsolatedDatabaseAsync(string databaseName)
    {
        var admin = new NpgsqlConnectionStringBuilder(Connection!)
        {
            Database = "postgres",
            Pooling = false
        };

        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();

        await using (var terminate = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE datname = @name AND pid <> pg_backend_pid()",
            connection))
        {
            terminate.Parameters.AddWithValue("name", databaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{databaseName}\"",
            connection);
        await drop.ExecuteNonQueryAsync();
    }

    private static ICEHOTTDbContext CreateContext(string connectionString) =>
        new(
            new DbContextOptionsBuilder<ICEHOTTDbContext>()
                .UseNpgsql(connectionString)
                .Options);
}
