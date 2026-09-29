using System.Text;
using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Artifacts;
using ICEHOTT.Application.Tools;
using ICEHOTT.Application.Workflows;
using ICEHOTT.Infrastructure.Artifacts;
using Microsoft.Extensions.Options;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using ICEHOTT.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ICEHOTT.Tests;

/// <summary>
/// Real-PostgreSQL proofs for the Phase 6A review findings: idempotency ownership,
/// tool-owning cancellation, and in-transaction artifact binding revalidation.
/// Like the other *PostgresTests, these need ICEHOTT_POSTGRES_TEST_CONNECTION.
/// </summary>
public sealed class WorkflowPhase6HardeningPostgresTests
{
    private static string? Connection =>
        Environment.GetEnvironmentVariable("ICEHOTT_POSTGRES_TEST_CONNECTION");

    [Fact]
    public async Task Idempotency_Key_Replay_By_Another_Member_Is_Conflict_Not_Disclosure()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, cs) = await CreateIsolatedDatabaseAsync();
        try
        {
            var seed = await SeedAsync(cs);
            var key = $"owner-{Guid.NewGuid():N}";

            await using var db1 = CreateContext(cs);
            var first = await new WorkflowAdministrationStore(db1).CreateRunAsync(
                seed.OwnerId, seed.WorkspaceId, seed.DefinitionId, key, seed.Now);
            Assert.Equal(WorkflowAdministrationPersistenceOutcome.Saved, first.Outcome);

            await using var db2 = CreateContext(cs);
            var stolen = await new WorkflowAdministrationStore(db2).CreateRunAsync(
                seed.MemberId, seed.WorkspaceId, seed.DefinitionId, key, seed.Now);
            Assert.Equal(
                WorkflowAdministrationPersistenceOutcome.IdempotencyKeyConflict,
                stolen.Outcome);
            Assert.Null(stolen.Run);

            await using var db3 = CreateContext(cs);
            var replay = await new WorkflowAdministrationStore(db3).CreateRunAsync(
                seed.OwnerId, seed.WorkspaceId, seed.DefinitionId, key, seed.Now);
            Assert.Equal(
                WorkflowAdministrationPersistenceOutcome.IdempotentReplay,
                replay.Outcome);
            Assert.Equal(first.Run!.Id, replay.Run!.Id);

            await using var verify = CreateContext(cs);
            Assert.Equal(1, await verify.WorkflowRuns.CountAsync(
                x => x.IdempotencyKey == key));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Cancel_Of_Waiting_Run_With_Tool_Execution_Defers_To_Runner()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, cs) = await CreateIsolatedDatabaseAsync();
        try
        {
            var seed = await SeedAsync(cs);
            var toolExecutionId = Guid.NewGuid();
            var (runId, _) = await SeedWaitingRunAsync(
                cs, seed, WorkflowStepType.Tool, WorkflowWaitReason.ToolExecution,
                WorkflowStepRunStatus.WaitingForTool, toolExecutionId);

            await using (var db = CreateContext(cs))
            {
                var result = await new WorkflowAdministrationStore(db).CancelRunAsync(
                    seed.OwnerId, seed.WorkspaceId, runId, seed.Now.AddMinutes(1));
                Assert.Equal(WorkflowAdministrationPersistenceOutcome.Saved, result.Outcome);
            }

            await using var verify = CreateContext(cs);
            var run = await verify.WorkflowRuns.SingleAsync(x => x.Id == runId);
            var step = await verify.WorkflowStepRuns.SingleAsync(x => x.WorkflowRunId == runId);
            // Not finalized: the runner must cancel the pending tool execution first.
            Assert.Equal(WorkflowRunStatus.Waiting, run.Status);
            Assert.NotNull(run.CancellationRequestedAtUtc);
            Assert.Equal(WorkflowStepRunStatus.WaitingForTool, step.Status);

            var events = await verify.WorkflowAuditEvents
                .Where(x => x.WorkflowRunId == runId)
                .Select(x => x.EventType).ToListAsync();
            Assert.Contains(WorkflowAuditEventType.CancellationRequested, events);
            Assert.DoesNotContain(WorkflowAuditEventType.RunCancelled, events);

            // ...and the durable queue still hands it to the runner.
            var queue = new WorkflowRunQueue(verify, new FixedClock(seed.Now.AddMinutes(2)));
            Assert.NotNull(await queue.LeaseNextAsync(Guid.NewGuid(), TimeSpan.FromSeconds(60)));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Cancel_Of_Waiting_Artifact_Run_Audits_Request_And_Cancellation()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, cs) = await CreateIsolatedDatabaseAsync();
        try
        {
            var seed = await SeedAsync(cs);
            var (runId, _) = await SeedWaitingRunAsync(
                cs, seed, WorkflowStepType.Artifact, WorkflowWaitReason.Artifact,
                WorkflowStepRunStatus.WaitingForArtifact, null);

            await using (var db = CreateContext(cs))
            {
                var result = await new WorkflowAdministrationStore(db).CancelRunAsync(
                    seed.OwnerId, seed.WorkspaceId, runId, seed.Now.AddMinutes(1));
                Assert.Equal(WorkflowAdministrationPersistenceOutcome.Saved, result.Outcome);
            }

            await using var verify = CreateContext(cs);
            var run = await verify.WorkflowRuns.SingleAsync(x => x.Id == runId);
            var step = await verify.WorkflowStepRuns.SingleAsync(x => x.WorkflowRunId == runId);
            Assert.Equal(WorkflowRunStatus.Cancelled, run.Status);
            Assert.Equal(WorkflowStepRunStatus.Cancelled, step.Status);
            var events = await verify.WorkflowAuditEvents
                .Where(x => x.WorkflowRunId == runId)
                .Select(x => x.EventType).ToListAsync();
            Assert.Equal(1, events.Count(x => x == WorkflowAuditEventType.CancellationRequested));
            Assert.Equal(1, events.Count(x => x == WorkflowAuditEventType.RunCancelled));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Bound_Artifact_Insert_Revalidates_Authorization_And_State()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, cs) = await CreateIsolatedDatabaseAsync();
        try
        {
            var seed = await SeedAsync(cs);
            var (runId, stepId) = await SeedWaitingRunAsync(
                cs, seed, WorkflowStepType.Artifact, WorkflowWaitReason.Artifact,
                WorkflowStepRunStatus.WaitingForArtifact, null);

            // A plain member who is not the run-as user may not satisfy the step.
            Assert.Equal(
                ArtifactAddOutcome.BindingNotAuthorized,
                await TryAddAsync(cs, seed, seed.MemberId, runId, stepId));

            // Partial/foreign bindings resolve as not found.
            Assert.Equal(
                ArtifactAddOutcome.BindingNotFound,
                await TryAddAsync(cs, seed, seed.OwnerId, runId, Guid.NewGuid()));
            Assert.Equal(
                ArtifactAddOutcome.BindingNotFound,
                await TryAddAsync(cs, seed, seed.OwnerId, Guid.NewGuid(), stepId));

            // The run-as user may.
            Assert.Equal(
                ArtifactAddOutcome.Added,
                await TryAddAsync(cs, seed, seed.OwnerId, runId, stepId));

            // Once a Ready artifact satisfies the step, a second upload is rejected.
            await using (var db = CreateContext(cs))
            {
                var artifact = await db.Artifacts.SingleAsync(x => x.StepRunId == stepId);
                artifact.MarkReady();
                await db.SaveChangesAsync();
            }
            Assert.Equal(
                ArtifactAddOutcome.BindingInvalidState,
                await TryAddAsync(cs, seed, seed.OwnerId, runId, stepId));

            // After cancellation the run is no longer waiting for the artifact.
            await using (var db = CreateContext(cs))
            {
                var result = await new WorkflowAdministrationStore(db).CancelRunAsync(
                    seed.OwnerId, seed.WorkspaceId, runId, seed.Now.AddMinutes(1));
                Assert.Equal(WorkflowAdministrationPersistenceOutcome.Saved, result.Outcome);
            }
            Assert.Equal(
                ArtifactAddOutcome.BindingInvalidState,
                await TryAddAsync(cs, seed, seed.OwnerId, runId, stepId));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Concurrent_Admin_Demotion_Blocks_Then_Rejects_Bound_Upload()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, cs) = await CreateIsolatedDatabaseAsync();
        try
        {
            var seed = await SeedAsync(cs);
            // Owner is the run-as user; let the *admin* satisfy the step instead.
            var adminId = Guid.NewGuid();
            await using (var setup = CreateContext(cs))
            {
                setup.Users.Add(NewUser(adminId, "Admin", seed.Now));
                setup.WorkspaceMemberships.Add(
                    new WorkspaceMembership(seed.WorkspaceId, adminId, WorkspaceRole.Admin, seed.Now));
                await setup.SaveChangesAsync();
            }

            var (runId, stepId) = await SeedWaitingRunAsync(
                cs, seed, WorkflowStepType.Artifact, WorkflowWaitReason.Artifact,
                WorkflowStepRunStatus.WaitingForArtifact, null);

            // Demotion is in flight (row-locked, uncommitted) when the upload arrives.
            await using var demotionConnection = new NpgsqlConnection(cs);
            await demotionConnection.OpenAsync();
            await using var demotion = await demotionConnection.BeginTransactionAsync();
            await using (var update = new NpgsqlCommand(
                "UPDATE workspace_memberships SET \"Role\" = 'Member' " +
                "WHERE \"WorkspaceId\" = @w AND \"UserId\" = @u",
                demotionConnection, demotion))
            {
                update.Parameters.AddWithValue("w", seed.WorkspaceId);
                update.Parameters.AddWithValue("u", adminId);
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
            }

            var upload = TryAddAsync(cs, seed, adminId, runId, stepId);
            await Task.Delay(750);
            Assert.False(upload.IsCompleted, "Upload must wait on the in-flight demotion.");

            await demotion.CommitAsync();
            Assert.Equal(ArtifactAddOutcome.BindingNotAuthorized, await upload);

            await using var verify = CreateContext(cs);
            Assert.False(await verify.Artifacts.AnyAsync(x => x.StepRunId == stepId));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Version_Activation_Retires_Previous_And_Never_Leaves_Two_Active()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, cs) = await CreateIsolatedDatabaseAsync();
        try
        {
            var seed = await SeedAsync(cs);
            const string json = """{"steps":[{"key":"a","type":"delay","delaySeconds":1}]}""";

            var ids = new List<Guid>();
            for (var i = 0; i < 3; i++)
            {
                await using var db = CreateContext(cs);
                var created = await new WorkflowAdministrationStore(db).CreateVersionAsync(
                    seed.OwnerId, seed.WorkspaceId, seed.DefinitionId, json,
                    new string((char)('a' + i), 64), seed.Now);
                Assert.Equal(WorkflowAdministrationPersistenceOutcome.Saved, created.Outcome);
                ids.Add(created.Version!.Id);
            }

            // Seeded version 1 is Active; activating v2 must retire it first.
            await using (var db = CreateContext(cs))
            {
                var activated = await new WorkflowAdministrationStore(db).ActivateVersionAsync(
                    seed.OwnerId, seed.WorkspaceId, seed.DefinitionId, ids[0], seed.Now);
                Assert.Equal(WorkflowAdministrationPersistenceOutcome.Saved, activated.Outcome);
            }

            // Two admins race to activate different drafts: exactly one ends up Active.
            await using var raceA = CreateContext(cs);
            await using var raceB = CreateContext(cs);
            var results = await Task.WhenAll(
                new WorkflowAdministrationStore(raceA).ActivateVersionAsync(
                    seed.OwnerId, seed.WorkspaceId, seed.DefinitionId, ids[1], seed.Now),
                new WorkflowAdministrationStore(raceB).ActivateVersionAsync(
                    seed.OwnerId, seed.WorkspaceId, seed.DefinitionId, ids[2], seed.Now));
            Assert.All(results, x => Assert.Equal(
                WorkflowAdministrationPersistenceOutcome.Saved, x.Outcome));

            await using var verify = CreateContext(cs);
            var versions = await verify.WorkflowVersions
                .Where(x => x.WorkflowDefinitionId == seed.DefinitionId).ToListAsync();
            Assert.Single(versions, x => x.Status == WorkflowVersionStatus.Active);
            Assert.Equal(3, versions.Count(x => x.Status == WorkflowVersionStatus.Retired));
            Assert.Equal(
                versions.Count - 1,
                await verify.WorkflowAuditEvents.CountAsync(
                    x => x.WorkflowDefinitionId == seed.DefinitionId &&
                         x.EventType == WorkflowAuditEventType.VersionRetired));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Artifact_Step_Waits_Then_Bound_Upload_Wakes_Queue_And_Completes_Redacted()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, cs) = await CreateIsolatedDatabaseAsync();
        var root = Path.Combine(
            Path.GetTempPath(), "icehott-p6-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            var seed = await SeedAsync(cs);
            var clock = new FixedClock(seed.Now.AddMinutes(5));

            Guid runId;
            await using (var db = CreateContext(cs))
            {
                var created = await new WorkflowAdministrationStore(db).CreateRunAsync(
                    seed.OwnerId, seed.WorkspaceId, seed.DefinitionId, "e2e", seed.Now);
                runId = created.Run!.Id;
            }

            // 1. The runner reaches the artifact step and parks the run.
            await using (var db = CreateContext(cs))
            {
                var queue = new WorkflowRunQueue(db, clock);
                var lease = Assert.IsType<WorkflowRunLease>(
                    await queue.LeaseNextAsync(Guid.NewGuid(), TimeSpan.FromSeconds(60)));
                var result = await CreateProcessor(db, queue, clock).ProcessAsync(lease);
                Assert.Equal(WorkflowRunStatus.Waiting, result.Status);
            }

            // 2. Nothing is due: the queue does not poll or spin on a waiting artifact.
            await using (var db = CreateContext(cs))
                Assert.Null(await new WorkflowRunQueue(db, clock)
                    .LeaseNextAsync(Guid.NewGuid(), TimeSpan.FromSeconds(60)));

            Guid stepId;
            await using (var db = CreateContext(cs))
                stepId = (await db.WorkflowStepRuns.SingleAsync(x => x.WorkflowRunId == runId)).Id;

            // 3. A bound upload through the real service makes the artifact Ready.
            const string secretBody = "TOP-SECRET-ARTIFACT-BODY-9f3c";
            var bytes = Encoding.UTF8.GetBytes(secretBody);
            ArtifactView uploaded;
            await using (var db = CreateContext(cs))
            {
                var upload = await CreateArtifactService(db, root, clock).UploadAsync(
                    seed.OwnerId, seed.WorkspaceId, "confidential-name.txt", "text/plain",
                    bytes.Length, new MemoryStream(bytes), "e2e-upload", runId, stepId);
                Assert.True(upload.Succeeded, upload.ErrorCode);
                uploaded = upload.Value!;
                Assert.Equal(ArtifactStatus.Ready, uploaded.Status);
            }

            // 4. The Ready artifact alone wakes the durable queue; the step completes
            //    and the run finishes.
            await using (var db = CreateContext(cs))
            {
                var queue = new WorkflowRunQueue(db, clock);
                var lease = Assert.IsType<WorkflowRunLease>(
                    await queue.LeaseNextAsync(Guid.NewGuid(), TimeSpan.FromSeconds(60)));
                var result = await CreateProcessor(db, queue, clock).ProcessAsync(lease);
                Assert.Equal(WorkflowRunStatus.Succeeded, result.Status);
            }

            await using var verify = CreateContext(cs);
            var step = await verify.WorkflowStepRuns.SingleAsync(x => x.Id == stepId);
            Assert.Equal(WorkflowStepRunStatus.Succeeded, step.Status);
            using (var output = JsonDocument.Parse(step.OutputJson!))
            {
                Assert.Equal(uploaded.Id, output.RootElement.GetProperty("artifactId").GetGuid());
                Assert.Equal(bytes.Length, output.RootElement.GetProperty("sizeBytes").GetInt64());
                Assert.Equal(uploaded.Sha256, output.RootElement.GetProperty("sha256").GetString());
            }

            // 5. Only redacted metadata is persisted: no content, no file name.
            var persisted = new List<string?> { step.OutputJson, step.InputJson, step.ErrorMessage };
            persisted.AddRange(await verify.WorkflowAuditEvents
                .Where(x => x.WorkflowRunId == runId)
                .Select(x => x.DetailJson).ToListAsync());
            foreach (var text in persisted.Where(x => x is not null))
            {
                Assert.DoesNotContain(secretBody, text!);
                Assert.DoesNotContain("confidential-name", text!);
            }

            // 6. After completion the step no longer accepts artifacts.
            await using (var db = CreateContext(cs))
            {
                var late = await CreateArtifactService(db, root, clock).UploadAsync(
                    seed.OwnerId, seed.WorkspaceId, "late.txt", "text/plain",
                    bytes.Length, new MemoryStream(bytes), "e2e-late", runId, stepId);
                Assert.Equal("workflow_binding_invalid_state", late.ErrorCode);
            }
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static ArtifactService CreateArtifactService(
        ICEHOTTDbContext db, string root, TimeProvider clock) =>
        new(new WorkspaceRepository(db), new ArtifactRepository(db),
            new WorkflowRepository(db),
            new LocalArtifactStore(Options.Create(
                new ArtifactStorageOptions { RootPath = root })),
            new WorkflowAuditRepository(db), db,
            new ArtifactPolicy(1024 * 1024, 4 * 1024 * 1024, 100, 30, 30, 1),
            clock);

    private static WorkflowRunProcessor CreateProcessor(
        ICEHOTTDbContext db, IWorkflowRunQueue queue, TimeProvider clock) =>
        new(new WorkflowRepository(db), queue, new WorkflowAuditRepository(db),
            new ArtifactRepository(db), new WorkspaceRepository(db),
            new UnusedToolInvoker(), clock);

    private sealed class UnusedToolInvoker : IWorkflowToolInvoker
    {
        public Task<ToolOperationResult<ToolExecutionView>> RequestAsync(
            Guid userId, Guid workspaceId, string toolName, JsonElement arguments,
            string idempotencyKey, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Artifact-only workflow must not call tools.");

        public Task<ToolOperationResult<ToolExecutionView>> GetAsync(
            Guid userId, Guid workspaceId, Guid executionId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Artifact-only workflow must not call tools.");

        public Task<ToolOperationResult<ToolExecutionView>> CancelAsync(
            Guid userId, Guid workspaceId, Guid executionId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Artifact-only workflow must not call tools.");
    }

    private static async Task<ArtifactAddOutcome> TryAddAsync(
        string cs, Seed seed, Guid userId, Guid runId, Guid stepId)
    {
        await using var db = CreateContext(cs);
        var id = Guid.NewGuid();
        var artifact = new Artifact(
            id, seed.WorkspaceId, userId, "result.txt", "text/plain", 4,
            new string('c', 64), $"workspaces/{seed.WorkspaceId:N}/artifacts/{id:N}",
            seed.Now, runId, stepId);
        return await new ArtifactRepository(db).TryAddWithinQuotaAsync(
            artifact, 100, 1024 * 1024);
    }

    private static async Task<(Guid RunId, Guid StepId)> SeedWaitingRunAsync(
        string cs, Seed seed, WorkflowStepType type, WorkflowWaitReason reason,
        WorkflowStepRunStatus stepStatus, Guid? toolExecutionId)
    {
        await using var db = CreateContext(cs);
        var version = await db.WorkflowVersions.SingleAsync(
            x => x.WorkflowDefinitionId == seed.DefinitionId);
        if (toolExecutionId is { } executionId)
            db.ToolExecutions.Add(new ToolExecution(
                executionId, seed.WorkspaceId, seed.OwnerId, "echo",
                ToolRiskLevel.SensitiveWrite, "{}", new string('b', 64),
                $"tool-{Guid.NewGuid():N}", requiresApproval: true, seed.Now));

        var run = new WorkflowRun(
            Guid.NewGuid(), seed.WorkspaceId, seed.DefinitionId, version.Id,
            seed.OwnerId, seed.OwnerId, $"seed-{Guid.NewGuid():N}", seed.Now);
        run.Start("step", seed.Now);
        var step = new WorkflowStepRun(
            Guid.NewGuid(), run.Id, seed.WorkspaceId, "step", 1, type, "{}");
        step.MarkReady();
        step.Start(seed.Now, toolExecutionId);
        switch (stepStatus)
        {
            case WorkflowStepRunStatus.WaitingForArtifact:
                step.WaitForArtifact();
                break;
            case WorkflowStepRunStatus.WaitingForTool:
                step.WaitForTool(toolExecutionId!.Value);
                break;
            default:
                throw new InvalidOperationException("Unsupported seed state.");
        }
        run.Wait(reason);
        db.WorkflowRuns.Add(run);
        db.WorkflowStepRuns.Add(step);
        await db.SaveChangesAsync();
        return (run.Id, step.Id);
    }

    private static async Task<Seed> SeedAsync(string cs)
    {
        await using var db = CreateContext(cs);
        await db.Database.MigrateAsync();

        var now = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        var ownerId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();

        db.Users.AddRange(NewUser(ownerId, "Owner", now), NewUser(memberId, "Member", now));
        db.Workspaces.Add(new Workspace(
            workspaceId, "Hardening Workspace", $"p6-hardening-{workspaceId:N}", ownerId, now));
        db.WorkspaceMemberships.AddRange(
            new WorkspaceMembership(workspaceId, ownerId, WorkspaceRole.Owner, now),
            new WorkspaceMembership(workspaceId, memberId, WorkspaceRole.Member, now));

        var definition = new WorkflowDefinition(
            definitionId, workspaceId, "Hardening workflow", null,
            WorkspaceRole.Member, ownerId, now);
        definition.MarkActive(now);
        db.WorkflowDefinitions.Add(definition);
        var version = new WorkflowVersion(
            Guid.NewGuid(), definitionId, workspaceId, 1,
            """{"steps":[{"key":"step","type":"artifact"}]}""",
            new string('a', 64), ownerId, now);
        version.Activate(now);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync();

        return new(ownerId, memberId, workspaceId, definitionId, now);
    }

    private static User NewUser(Guid id, string name, DateTimeOffset now) =>
        new(id, $"{id:N}@p6-hardening.test", name, "not-a-real-hash", now);

    private static ICEHOTTDbContext CreateContext(string cs) =>
        new(new DbContextOptionsBuilder<ICEHOTTDbContext>().UseNpgsql(cs).Options);

    private static async Task<(string DatabaseName, string ConnectionString)>
        CreateIsolatedDatabaseAsync()
    {
        var source = new NpgsqlConnectionStringBuilder(Connection!);
        var databaseName = $"icehott_p6_hardening_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(Connection!)
        {
            Database = "postgres",
            Pooling = false
        };

        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command =
            new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
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
            "WHERE datname = @name AND pid <> pg_backend_pid()", connection))
        {
            terminate.Parameters.AddWithValue("name", databaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop =
            new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\"", connection);
        await drop.ExecuteNonQueryAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Seed(
        Guid OwnerId,
        Guid MemberId,
        Guid WorkspaceId,
        Guid DefinitionId,
        DateTimeOffset Now);
}
