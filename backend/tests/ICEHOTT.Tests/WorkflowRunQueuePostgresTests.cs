using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using ICEHOTT.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ICEHOTT.Tests;

public sealed class WorkflowRunQueuePostgresTests
{
    private static string? Connection =>
        Environment.GetEnvironmentVariable("ICEHOTT_POSTGRES_TEST_CONNECTION");

    [Fact]
    public async Task Concurrent_Workers_Claim_One_Run_Only_Once()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedRunAsync(isolatedConnection);

            await using var db1 = CreateContext(isolatedConnection);
            await using var db2 = CreateContext(isolatedConnection);
            var queue1 = new WorkflowRunQueue(db1, TimeProvider.System);
            var queue2 = new WorkflowRunQueue(db2, TimeProvider.System);

            var claims = await Task.WhenAll(
                queue1.LeaseNextAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30)),
                queue2.LeaseNextAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30)));

            Assert.Single(claims, x => x is not null);

            await using var verify = CreateContext(isolatedConnection);
            var run = await verify.WorkflowRuns.SingleAsync(x => x.Id == seed.RunId);
            Assert.Equal(WorkflowRunStatus.Running, run.Status);
            Assert.Equal(1, run.LeaseGeneration);
            Assert.NotNull(run.LeaseOwnerId);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task Stale_Worker_Is_Fenced_After_Run_Is_Reclaimed()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedRunAsync(isolatedConnection);
            var worker1 = Guid.NewGuid();
            var worker2 = Guid.NewGuid();

            await using var db1 = CreateContext(isolatedConnection);
            var queue1 = new WorkflowRunQueue(db1, TimeProvider.System);
            var lease1 = Assert.IsType<WorkflowRunLease>(
                await queue1.LeaseNextAsync(worker1, TimeSpan.FromSeconds(30)));

            var staleTrackedRun = await db1.WorkflowRuns.SingleAsync(
                x => x.Id == seed.RunId);

            await ExpireLeaseAsync(isolatedConnection, seed.RunId);

            await using var db2 = CreateContext(isolatedConnection);
            var queue2 = new WorkflowRunQueue(db2, TimeProvider.System);
            var lease2 = Assert.IsType<WorkflowRunLease>(
                await queue2.LeaseNextAsync(worker2, TimeSpan.FromSeconds(30)));

            Assert.True(lease2.LeaseGeneration > lease1.LeaseGeneration);

            staleTrackedRun.Fail(
                "stale_worker_should_not_commit",
                null,
                DateTimeOffset.UtcNow);

            var staleSave = await queue1.SaveFencedAsync(
                seed.RunId,
                worker1,
                lease1.LeaseGeneration);

            Assert.Equal(WorkflowRunPersistenceOutcome.LeaseLost, staleSave);

            await using var verify = CreateContext(isolatedConnection);
            var run = await verify.WorkflowRuns.SingleAsync(x => x.Id == seed.RunId);
            Assert.Equal(WorkflowRunStatus.Running, run.Status);
            Assert.Equal(worker2, run.LeaseOwnerId);
            Assert.Equal(lease2.LeaseGeneration, run.LeaseGeneration);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task Heartbeat_Requires_Current_Fencing_Generation()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedRunAsync(isolatedConnection);
            var worker = Guid.NewGuid();

            await using var db = CreateContext(isolatedConnection);
            var queue = new WorkflowRunQueue(db, TimeProvider.System);
            var lease = Assert.IsType<WorkflowRunLease>(
                await queue.LeaseNextAsync(worker, TimeSpan.FromSeconds(30)));

            Assert.False(await queue.RenewLeaseAsync(
                seed.RunId,
                worker,
                lease.LeaseGeneration + 1,
                TimeSpan.FromSeconds(30)));

            Assert.True(await queue.RenewLeaseAsync(
                seed.RunId,
                worker,
                lease.LeaseGeneration,
                TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Due_Delay_Wait_Is_Resumed_But_Future_Delay_Is_Not()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var due = await SeedRunAsync(isolatedConnection, "due");
            var future = await SeedRunAsync(isolatedConnection, "future");

            await using (var setup = CreateContext(isolatedConnection))
            {
                var now = DateTimeOffset.UtcNow;
                var dueRun = await setup.WorkflowRuns.SingleAsync(x => x.Id == due.RunId);
                dueRun.Start("delay", now.AddMinutes(-2));
                dueRun.Wait(WorkflowWaitReason.Delay, now.AddSeconds(-1));

                var futureRun = await setup.WorkflowRuns.SingleAsync(x => x.Id == future.RunId);
                futureRun.Start("delay", now.AddMinutes(-2));
                futureRun.Wait(WorkflowWaitReason.Delay, now.AddMinutes(10));
                await setup.SaveChangesAsync();
            }

            await using var db = CreateContext(isolatedConnection);
            var queue = new WorkflowRunQueue(db, TimeProvider.System);
            var lease = Assert.IsType<WorkflowRunLease>(
                await queue.LeaseNextAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30)));

            Assert.Equal(due.RunId, lease.RunId);

            await using var verify = CreateContext(isolatedConnection);
            var dueAfter = await verify.WorkflowRuns.SingleAsync(x => x.Id == due.RunId);
            var futureAfter = await verify.WorkflowRuns.SingleAsync(x => x.Id == future.RunId);

            Assert.Equal(WorkflowRunStatus.Running, dueAfter.Status);
            Assert.Null(dueAfter.WaitReason);
            Assert.Null(dueAfter.ResumeAtUtc);
            Assert.Equal(WorkflowRunStatus.Waiting, futureAfter.Status);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task Cancellation_Request_Wakes_Checkpoint_Wait_Durably()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedRunAsync(isolatedConnection);
            var now = DateTimeOffset.UtcNow;

            await using (var setup = CreateContext(isolatedConnection))
            {
                var run = await setup.WorkflowRuns.SingleAsync(x => x.Id == seed.RunId);
                run.Start("checkpoint", now.AddMinutes(-1));
                run.Wait(WorkflowWaitReason.Checkpoint);
                await setup.SaveChangesAsync();
            }

            var worker = Guid.NewGuid();
            await using var db = CreateContext(isolatedConnection);
            var queue = new WorkflowRunQueue(db, TimeProvider.System);

            Assert.True(await queue.RequestCancellationAsync(
                seed.WorkspaceId,
                seed.RunId,
                seed.OwnerId,
                now));

            var lease = Assert.IsType<WorkflowRunLease>(
                await queue.LeaseNextAsync(worker, TimeSpan.FromSeconds(30)));

            Assert.Equal(seed.RunId, lease.RunId);
            Assert.True(await queue.IsCancellationRequestedAsync(
                seed.RunId,
                worker,
                lease.LeaseGeneration));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Expired_Run_With_Running_Tool_Is_Not_Replayed()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedRunAsync(isolatedConnection);
            var toolExecutionId = Guid.NewGuid();
            var oldWorker = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            await using (var setup = CreateContext(isolatedConnection))
            {
                var run = await setup.WorkflowRuns.SingleAsync(x => x.Id == seed.RunId);
                run.Start("tool", now.AddMinutes(-2));
                run.ClaimLease(oldWorker, now.AddSeconds(-1));

                var tool = new ToolExecution(
                    toolExecutionId,
                    seed.WorkspaceId,
                    seed.OwnerId,
                    "workspace.echo",
                    ToolRiskLevel.ReadOnly,
                    "{\"text\":\"hello\"}",
                    Hash('a'),
                    "workflow-tool-running",
                    false,
                    now.AddMinutes(-2));
                tool.Start(
                    now.AddMinutes(-2),
                    Guid.NewGuid(),
                    now.AddMinutes(-1),
                    now.AddSeconds(-1));
                setup.ToolExecutions.Add(tool);

                var step = new WorkflowStepRun(
                    Guid.NewGuid(),
                    seed.RunId,
                    seed.WorkspaceId,
                    "tool",
                    1,
                    WorkflowStepType.Tool,
                    "{}");
                step.MarkReady();
                step.Start(now.AddMinutes(-2), toolExecutionId);
                setup.WorkflowStepRuns.Add(step);

                await setup.SaveChangesAsync();
            }

            await using var db = CreateContext(isolatedConnection);
            var queue = new WorkflowRunQueue(db, TimeProvider.System);

            Assert.Null(await queue.LeaseNextAsync(
                Guid.NewGuid(),
                TimeSpan.FromSeconds(30)));

            await using (var reconcile = CreateContext(isolatedConnection))
            {
                var tool = await reconcile.ToolExecutions.SingleAsync(
                    x => x.Id == toolExecutionId);
                tool.MarkOutcomeUnknown(DateTimeOffset.UtcNow);
                await reconcile.SaveChangesAsync();
            }

            Assert.NotNull(await queue.LeaseNextAsync(
                Guid.NewGuid(),
                TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task Tool_Approval_Wait_Wakes_Only_After_Tool_Is_Terminal()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedRunAsync(isolatedConnection);
            var toolExecutionId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            await using (var setup = CreateContext(isolatedConnection))
            {
                var run = await setup.WorkflowRuns.SingleAsync(x => x.Id == seed.RunId);
                run.Start("tool", now.AddMinutes(-1));
                run.Wait(WorkflowWaitReason.ToolExecution);

                var tool = new ToolExecution(
                    toolExecutionId,
                    seed.WorkspaceId,
                    seed.OwnerId,
                    "workspace.audit-note.create",
                    ToolRiskLevel.SensitiveWrite,
                    "{\"message\":\"test\"}",
                    Hash('b'),
                    "workflow-tool-approval",
                    true,
                    now.AddMinutes(-1));
                setup.ToolExecutions.Add(tool);

                var step = new WorkflowStepRun(
                    Guid.NewGuid(),
                    seed.RunId,
                    seed.WorkspaceId,
                    "tool",
                    1,
                    WorkflowStepType.Tool,
                    "{}");
                step.MarkReady();
                step.Start(now.AddMinutes(-1));
                step.WaitForTool(toolExecutionId);
                setup.WorkflowStepRuns.Add(step);
                await setup.SaveChangesAsync();
            }

            await using var db = CreateContext(isolatedConnection);
            var queue = new WorkflowRunQueue(db, TimeProvider.System);

            Assert.Null(await queue.LeaseNextAsync(
                Guid.NewGuid(),
                TimeSpan.FromSeconds(30)));

            await using (var reject = CreateContext(isolatedConnection))
            {
                var tool = await reject.ToolExecutions.SingleAsync(
                    x => x.Id == toolExecutionId);
                tool.Reject(seed.OtherUserId, DateTimeOffset.UtcNow);
                await reject.SaveChangesAsync();
            }

            var lease = await queue.LeaseNextAsync(
                Guid.NewGuid(),
                TimeSpan.FromSeconds(30));
            Assert.NotNull(lease);
            Assert.Equal(seed.RunId, lease!.RunId);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task Phase5C_Migration_Rolls_Back_To_5B_And_Reapplies()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            await using var db = CreateContext(isolatedConnection);
            await db.Database.MigrateAsync();

            Assert.True(await ColumnExistsAsync(
                db,
                "workflow_runs",
                "CancellationRequestedAtUtc"));

            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(
                "20260925233754_Phase5WorkflowsFoundation");

            Assert.False(await ColumnExistsAsync(
                db,
                "workflow_runs",
                "CancellationRequestedAtUtc"));
            Assert.True(await TableExistsAsync(db, "workflow_runs"));

            await migrator.MigrateAsync();

            Assert.True(await ColumnExistsAsync(
                db,
                "workflow_runs",
                "CancellationRequestedAtUtc"));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    private static Task<bool> TableExistsAsync(
        ICEHOTTDbContext db,
        string tableName) =>
        db.Database.SqlQueryRaw<bool>(
            """
            SELECT to_regclass({0}) IS NOT NULL AS "Value"
            """,
            $"public.{tableName}").SingleAsync();

    private static Task<bool> ColumnExistsAsync(
        ICEHOTTDbContext db,
        string tableName,
        string columnName) =>
        db.Database.SqlQueryRaw<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = {0}
                  AND column_name = {1}
            ) AS "Value"
            """,
            tableName,
            columnName).SingleAsync();

    private sealed record Seed(
        Guid OwnerId,
        Guid OtherUserId,
        Guid WorkspaceId,
        Guid DefinitionId,
        Guid VersionId,
        Guid RunId);

    private static async Task<Seed> SeedRunAsync(
        string connectionString,
        string suffix = "default")
    {
        await using var db = CreateContext(connectionString);
        await db.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var ownerId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.Users.AddRange(
            new User(
                ownerId,
                $"{ownerId:N}@queue.test",
                "Queue Owner",
                "not-a-real-hash",
                now),
            new User(
                otherUserId,
                $"{otherUserId:N}@queue.test",
                "Other User",
                "not-a-real-hash",
                now));

        db.Workspaces.Add(new Workspace(
            workspaceId,
            $"Queue {suffix}",
            $"queue-{suffix}-{workspaceId:N}",
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
            $"Workflow {suffix}",
            null,
            WorkspaceRole.Member,
            ownerId,
            now));

        var version = new WorkflowVersion(
            versionId,
            definitionId,
            workspaceId,
            1,
            "{\"steps\":[]}",
            Hash('z'),
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
            $"queue-{suffix}-{runId:N}",
            now));

        await db.SaveChangesAsync();

        return new Seed(
            ownerId,
            otherUserId,
            workspaceId,
            definitionId,
            versionId,
            runId);
    }

    private static async Task ExpireLeaseAsync(
        string connectionString,
        Guid runId)
    {
        await using var db = CreateContext(connectionString);
        await db.WorkflowRuns
            .Where(x => x.Id == runId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                x => x.LeaseExpiresAtUtc,
                DateTimeOffset.UtcNow.AddSeconds(-5)));
    }

    private static string Hash(char value) => new(value, 64);
    private static async Task<(string DatabaseName, string ConnectionString)>
        CreateIsolatedDatabaseAsync()
    {
        var source = new NpgsqlConnectionStringBuilder(Connection!);
        var databaseName = $"icehott_runner_{Guid.NewGuid():N}";
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
