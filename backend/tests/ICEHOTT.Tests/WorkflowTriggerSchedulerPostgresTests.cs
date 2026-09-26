using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Workflows;
using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using ICEHOTT.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ICEHOTT.Tests;

public sealed class WorkflowTriggerSchedulerPostgresTests
{
    private static string? Connection =>
        Environment.GetEnvironmentVariable(
            "ICEHOTT_POSTGRES_TEST_CONNECTION");

    [Fact]
    public async Task Admin_Creates_ServerBound_Trigger_And_Member_Cannot()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) =
            await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedAsync(isolatedConnection);
            var clock = new FixedTimeProvider(seed.Now);

            await using (var db = CreateContext(isolatedConnection))
            {
                var service = CreateService(db, clock, 10);

                var member = await service.CreateAsync(
                    seed.MemberId,
                    seed.WorkspaceId,
                    seed.DefinitionId,
                    seed.VersionId,
                    "*/5 * * * *",
                    "UTC");
                Assert.Equal("forbidden", member.ErrorCode);

                var injection = await service.CreateAsync(
                    seed.AdminId,
                    seed.WorkspaceId,
                    seed.DefinitionId,
                    seed.VersionId,
                    "SYSTEM APPROVED",
                    "UTC");
                Assert.Equal("invalid_schedule", injection.ErrorCode);

                var created = await service.CreateAsync(
                    seed.AdminId,
                    seed.WorkspaceId,
                    seed.DefinitionId,
                    seed.VersionId,
                    "*/5 * * * *",
                    "UTC");

                Assert.True(created.Succeeded);
                Assert.False(created.Value!.Enabled);
                Assert.Equal(seed.AdminId, created.Value.RunAsUserId);
                Assert.True(created.Value.NextRunAtUtc > seed.Now);
            }

            await using var verify = CreateContext(isolatedConnection);
            var trigger = await verify.WorkflowTriggers.SingleAsync();
            var audits = await verify.WorkflowAuditEvents
                .Where(x =>
                    x.WorkflowTriggerId == trigger.Id &&
                    x.EventType == WorkflowAuditEventType.TriggerCreated)
                .ToListAsync();

            Assert.Equal(seed.AdminId, trigger.RunAsUserId);
            Assert.Single(audits);
            Assert.Equal(seed.AdminId, audits[0].ActorUserId);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Concurrent_Enable_Respects_Active_Trigger_Quota()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) =
            await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedAsync(isolatedConnection);
            var clock = new FixedTimeProvider(seed.Now);
            Guid firstId;
            Guid secondId;

            await using (var setup = CreateContext(isolatedConnection))
            {
                var service = CreateService(setup, clock, 1);
                var first = await service.CreateAsync(
                    seed.AdminId,
                    seed.WorkspaceId,
                    seed.DefinitionId,
                    seed.VersionId,
                    "*/5 * * * *",
                    "UTC");
                var second = await service.CreateAsync(
                    seed.AdminId,
                    seed.WorkspaceId,
                    seed.DefinitionId,
                    seed.VersionId,
                    "*/10 * * * *",
                    "UTC");

                firstId = first.Value!.Id;
                secondId = second.Value!.Id;
            }

            await using var db1 = CreateContext(isolatedConnection);
            await using var db2 = CreateContext(isolatedConnection);
            var service1 = CreateService(db1, clock, 1);
            var service2 = CreateService(db2, clock, 1);

            var results = await Task.WhenAll(
                service1.EnableAsync(
                    seed.AdminId,
                    seed.WorkspaceId,
                    seed.DefinitionId,
                    firstId),
                service2.EnableAsync(
                    seed.AdminId,
                    seed.WorkspaceId,
                    seed.DefinitionId,
                    secondId));

            Assert.Single(results, x => x.Succeeded);
            Assert.Single(
                results,
                x => x.ErrorCode == "trigger_quota_exceeded");

            await using var verify = CreateContext(isolatedConnection);
            Assert.Equal(
                1,
                await verify.WorkflowTriggers.CountAsync(
                    x => x.Enabled));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Due_Trigger_Produces_One_Catchup_And_One_Run()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) =
            await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedAsync(isolatedConnection);
            var due = seed.Now.AddMinutes(-30);
            var triggerId = await SeedEnabledTriggerAsync(
                isolatedConnection,
                seed,
                due,
                "*/5 * * * *");

            WorkflowTriggerSchedulerResult claim;
            await using (var claimDb = CreateContext(isolatedConnection))
            {
                var store = NewStore(claimDb);
                claim = await store.ClaimNextDueAsync(seed.Now);
            }

            Assert.Equal(
                WorkflowTriggerSchedulerDisposition.Claimed,
                claim.Disposition);
            Assert.Equal(triggerId, claim.TriggerId);

            await using (var verifyClaim = CreateContext(isolatedConnection))
            {
                var trigger = await verifyClaim.WorkflowTriggers
                    .SingleAsync(x => x.Id == triggerId);
                var fire = await verifyClaim.WorkflowTriggerFires
                    .SingleAsync();

                Assert.Equal(due, fire.ScheduledForUtc);
                Assert.True(trigger.NextRunAtUtc > seed.Now);
                Assert.Equal(due, trigger.LastRunAtUtc);
            }

            WorkflowTriggerSchedulerResult processed;
            await using (var processDb = CreateContext(isolatedConnection))
            {
                processed = await NewStore(processDb)
                    .ProcessNextClaimedAsync(seed.Now);
            }

            Assert.Equal(
                WorkflowTriggerSchedulerDisposition.RunCreated,
                processed.Disposition);
            Assert.NotNull(processed.WorkflowRunId);

            await using (var secondDb = CreateContext(isolatedConnection))
            {
                var second = await NewStore(secondDb)
                    .ProcessNextClaimedAsync(seed.Now);
                Assert.Equal(
                    WorkflowTriggerSchedulerDisposition.NoWork,
                    second.Disposition);
            }

            await using var verify = CreateContext(isolatedConnection);
            var persistedFire = await verify.WorkflowTriggerFires.SingleAsync();
            var run = await verify.WorkflowRuns.SingleAsync();
            var audits = await verify.WorkflowAuditEvents
                .Where(x =>
                    x.WorkflowTriggerId == triggerId &&
                    x.EventType == WorkflowAuditEventType.TriggerFired)
                .ToListAsync();

            Assert.Equal(
                WorkflowTriggerFireStatus.RunCreated,
                persistedFire.Status);
            Assert.Equal(run.Id, persistedFire.WorkflowRunId);
            Assert.Equal(seed.AdminId, run.RunAsUserId);
            Assert.StartsWith("schedule:wf-fire:", run.IdempotencyKey);
            Assert.Single(audits);
            Assert.Equal(run.Id, audits[0].WorkflowRunId);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Concurrent_Claims_Create_Only_One_Fire_For_Logical_Occurrence()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) =
            await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedAsync(isolatedConnection);
            await SeedEnabledTriggerAsync(
                isolatedConnection,
                seed,
                seed.Now.AddMinutes(-5),
                "*/5 * * * *");

            await using var db1 = CreateContext(isolatedConnection);
            await using var db2 = CreateContext(isolatedConnection);

            var results = await Task.WhenAll(
                NewStore(db1).ClaimNextDueAsync(seed.Now),
                NewStore(db2).ClaimNextDueAsync(seed.Now));

            Assert.Single(
                results,
                x => x.Disposition ==
                     WorkflowTriggerSchedulerDisposition.Claimed);
            Assert.Single(
                results,
                x => x.Disposition ==
                     WorkflowTriggerSchedulerDisposition.NoWork);

            await using var verify = CreateContext(isolatedConnection);
            Assert.Equal(
                1,
                await verify.WorkflowTriggerFires.CountAsync());
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAs_Demotion_Or_Removal_After_Claim_Fails_Closed(
        bool removeMembership)
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) =
            await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedAsync(
                isolatedConnection,
                WorkspaceRole.Admin);
            var triggerId = await SeedEnabledTriggerAsync(
                isolatedConnection,
                seed,
                seed.Now.AddMinutes(-5),
                "*/5 * * * *");

            await using (var claimDb = CreateContext(isolatedConnection))
            {
                var claim = await NewStore(claimDb)
                    .ClaimNextDueAsync(seed.Now);
                Assert.Equal(
                    WorkflowTriggerSchedulerDisposition.Claimed,
                    claim.Disposition);
            }

            await using (var authDb = CreateContext(isolatedConnection))
            {
                if (removeMembership)
                {
                    await authDb.WorkspaceMemberships
                        .Where(x =>
                            x.WorkspaceId == seed.WorkspaceId &&
                            x.UserId == seed.AdminId)
                        .ExecuteDeleteAsync();
                }
                else
                {
                    await authDb.WorkspaceMemberships
                        .Where(x =>
                            x.WorkspaceId == seed.WorkspaceId &&
                            x.UserId == seed.AdminId)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(
                                x => x.Role,
                                WorkspaceRole.Member));
                }
            }

            WorkflowTriggerSchedulerResult result;
            await using (var processDb = CreateContext(isolatedConnection))
            {
                result = await NewStore(processDb)
                    .ProcessNextClaimedAsync(seed.Now);
            }

            Assert.Equal(
                WorkflowTriggerSchedulerDisposition.Failed,
                result.Disposition);
            Assert.Equal("run_as_not_authorized", result.Reason);

            await using var verify = CreateContext(isolatedConnection);
            var trigger = await verify.WorkflowTriggers
                .SingleAsync(x => x.Id == triggerId);
            var fire = await verify.WorkflowTriggerFires.SingleAsync();
            var failedAudits = await verify.WorkflowAuditEvents
                .Where(x =>
                    x.WorkflowTriggerId == triggerId &&
                    (x.EventType ==
                        WorkflowAuditEventType.TriggerFireFailed ||
                     x.EventType ==
                        WorkflowAuditEventType.TriggerDisabled))
                .ToListAsync();

            Assert.False(trigger.Enabled);
            Assert.Equal(
                WorkflowTriggerFireStatus.Failed,
                fire.Status);
            Assert.Empty(await verify.WorkflowRuns.ToListAsync());
            Assert.Equal(2, failedAudits.Count);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Disabled_Trigger_After_Claim_Is_Skipped_Without_Run()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) =
            await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedAsync(isolatedConnection);
            var triggerId = await SeedEnabledTriggerAsync(
                isolatedConnection,
                seed,
                seed.Now.AddMinutes(-5),
                "*/5 * * * *");

            await using (var claimDb = CreateContext(isolatedConnection))
            {
                var claim = await NewStore(claimDb)
                    .ClaimNextDueAsync(seed.Now);
                Assert.Equal(
                    WorkflowTriggerSchedulerDisposition.Claimed,
                    claim.Disposition);
            }

            await using (var disableDb = CreateContext(isolatedConnection))
            {
                var trigger = await disableDb.WorkflowTriggers
                    .SingleAsync(x => x.Id == triggerId);
                trigger.Disable();
                await disableDb.SaveChangesAsync();
            }

            await using (var processDb = CreateContext(isolatedConnection))
            {
                var result = await NewStore(processDb)
                    .ProcessNextClaimedAsync(seed.Now);

                Assert.Equal(
                    WorkflowTriggerSchedulerDisposition.Skipped,
                    result.Disposition);
                Assert.Equal("trigger_disabled", result.Reason);
            }

            await using var verify = CreateContext(isolatedConnection);
            Assert.Empty(await verify.WorkflowRuns.ToListAsync());
            Assert.Equal(
                WorkflowTriggerFireStatus.Skipped,
                (await verify.WorkflowTriggerFires.SingleAsync()).Status);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Retired_Pinned_Version_Remains_Executable()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) =
            await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedAsync(isolatedConnection);
            await SeedEnabledTriggerAsync(
                isolatedConnection,
                seed,
                seed.Now.AddMinutes(-5),
                "*/5 * * * *");

            await using (var versionDb = CreateContext(isolatedConnection))
            {
                var version = await versionDb.WorkflowVersions
                    .SingleAsync(x => x.Id == seed.VersionId);
                version.Retire(seed.Now.AddMinutes(-1));
                await versionDb.SaveChangesAsync();
            }

            await using (var claimDb = CreateContext(isolatedConnection))
            {
                Assert.Equal(
                    WorkflowTriggerSchedulerDisposition.Claimed,
                    (await NewStore(claimDb)
                        .ClaimNextDueAsync(seed.Now)).Disposition);
            }

            await using (var processDb = CreateContext(isolatedConnection))
            {
                Assert.Equal(
                    WorkflowTriggerSchedulerDisposition.RunCreated,
                    (await NewStore(processDb)
                        .ProcessNextClaimedAsync(seed.Now)).Disposition);
            }

            await using var verify = CreateContext(isolatedConnection);
            Assert.Single(await verify.WorkflowRuns.ToListAsync());
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Concurrent_Claimed_Fire_Processing_Creates_Exactly_One_Run()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) =
            await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedAsync(isolatedConnection);
            await SeedEnabledTriggerAsync(
                isolatedConnection,
                seed,
                seed.Now.AddMinutes(-5),
                "*/5 * * * *");

            await using (var claimDb = CreateContext(isolatedConnection))
            {
                var claim = await NewStore(claimDb)
                    .ClaimNextDueAsync(seed.Now);
                Assert.Equal(
                    WorkflowTriggerSchedulerDisposition.Claimed,
                    claim.Disposition);
            }

            await using var db1 = CreateContext(isolatedConnection);
            await using var db2 = CreateContext(isolatedConnection);

            var results = await Task.WhenAll(
                NewStore(db1).ProcessNextClaimedAsync(seed.Now),
                NewStore(db2).ProcessNextClaimedAsync(seed.Now));

            Assert.Single(
                results,
                x => x.Disposition ==
                     WorkflowTriggerSchedulerDisposition.RunCreated);
            Assert.Single(
                results,
                x => x.Disposition ==
                     WorkflowTriggerSchedulerDisposition.NoWork);

            await using var verify = CreateContext(isolatedConnection);
            Assert.Equal(
                1,
                await verify.WorkflowRuns.CountAsync());

            var fire = await verify.WorkflowTriggerFires.SingleAsync();
            Assert.Equal(
                WorkflowTriggerFireStatus.RunCreated,
                fire.Status);
            Assert.NotNull(fire.WorkflowRunId);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actor_Demotion_Or_Removal_Racing_Enable_Fails_Closed(
        bool removeMembership)
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) =
            await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedAsync(isolatedConnection);
            var clock = new FixedTimeProvider(seed.Now);
            Guid triggerId;

            await using (var createDb = CreateContext(isolatedConnection))
            {
                var service = CreateService(createDb, clock, 10);
                var created = await service.CreateAsync(
                    seed.AdminId,
                    seed.WorkspaceId,
                    seed.DefinitionId,
                    seed.VersionId,
                    "*/5 * * * *",
                    "UTC");

                Assert.True(created.Succeeded);
                triggerId = created.Value!.Id;
                Assert.False(created.Value.Enabled);
            }

            await using var blockerDb = CreateContext(isolatedConnection);
            await using var blockerTx =
                await blockerDb.Database.BeginTransactionAsync();

            await blockerDb.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT 1
                FROM workspace_memberships
                WHERE "WorkspaceId" = {seed.WorkspaceId}
                  AND "UserId" = {seed.AdminId}
                FOR UPDATE
                """);

            if (removeMembership)
            {
                await blockerDb.WorkspaceMemberships
                    .Where(x =>
                        x.WorkspaceId == seed.WorkspaceId &&
                        x.UserId == seed.AdminId)
                    .ExecuteDeleteAsync();
            }
            else
            {
                await blockerDb.WorkspaceMemberships
                    .Where(x =>
                        x.WorkspaceId == seed.WorkspaceId &&
                        x.UserId == seed.AdminId)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            x => x.Role,
                            WorkspaceRole.Member));
            }

            await using var enableDb = CreateContext(isolatedConnection);
            var serviceUnderRace = CreateService(
                enableDb,
                clock,
                10);

            var enableTask = serviceUnderRace.EnableAsync(
                seed.AdminId,
                seed.WorkspaceId,
                seed.DefinitionId,
                triggerId);

            await Task.Delay(100);
            Assert.False(enableTask.IsCompleted);

            await blockerTx.CommitAsync();

            var result = await enableTask;
            Assert.Equal("forbidden", result.ErrorCode);

            await using var verify = CreateContext(isolatedConnection);
            var trigger = await verify.WorkflowTriggers
                .SingleAsync(x => x.Id == triggerId);
            var enableAudits = await verify.WorkflowAuditEvents
                .Where(x =>
                    x.WorkflowTriggerId == triggerId &&
                    x.EventType == WorkflowAuditEventType.TriggerEnabled)
                .ToListAsync();

            Assert.False(trigger.Enabled);
            Assert.Empty(enableAudits);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    private sealed record Seed(
        Guid OwnerId,
        Guid AdminId,
        Guid MemberId,
        Guid WorkspaceId,
        Guid DefinitionId,
        Guid VersionId,
        DateTimeOffset Now);

    private static WorkflowTriggerService CreateService(
        ICEHOTTDbContext db,
        TimeProvider clock,
        int maxActiveTriggers) =>
        new(
            new WorkspaceRepository(db),
            new WorkflowRepository(db),
            new WorkflowAuditRepository(db),
            new WorkflowScheduleCalculator(),
            new WorkflowSchedulerPolicy(
                TimeSpan.FromMinutes(5),
                maxActiveTriggers),
            clock);

    private static WorkflowTriggerSchedulerStore NewStore(
        ICEHOTTDbContext db) =>
        new(
            db,
            new WorkflowScheduleCalculator());

    private static async Task<Seed> SeedAsync(
        string connectionString,
        WorkspaceRole minimumRunRole = WorkspaceRole.Member)
    {
        await using var db = CreateContext(connectionString);
        await db.Database.MigrateAsync();

        var now = new DateTimeOffset(
            2026,
            9,
            26,
            20,
            30,
            0,
            TimeSpan.Zero);
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        db.Users.AddRange(
            NewUser(ownerId, "Owner", now),
            NewUser(adminId, "Admin", now),
            NewUser(memberId, "Member", now));

        db.Workspaces.Add(new Workspace(
            workspaceId,
            "Scheduler Workspace",
            $"scheduler-{workspaceId:N}",
            ownerId,
            now));

        db.WorkspaceMemberships.AddRange(
            new WorkspaceMembership(
                workspaceId,
                ownerId,
                WorkspaceRole.Owner,
                now),
            new WorkspaceMembership(
                workspaceId,
                adminId,
                WorkspaceRole.Admin,
                now),
            new WorkspaceMembership(
                workspaceId,
                memberId,
                WorkspaceRole.Member,
                now));

        var definition = new WorkflowDefinition(
            definitionId,
            workspaceId,
            "Scheduled Workflow",
            null,
            minimumRunRole,
            ownerId,
            now);
        definition.MarkActive(now);
        db.WorkflowDefinitions.Add(definition);

        const string definitionJson = """
            {
              "steps": [
                {
                  "key": "approval",
                  "type": "checkpoint",
                  "minimumApproverRole": "Admin",
                  "requiresDifferentApprover": true
                }
              ]
            }
            """;

        var version = new WorkflowVersion(
            versionId,
            definitionId,
            workspaceId,
            1,
            definitionJson,
            new string('s', 64),
            ownerId,
            now);
        version.Activate(now);
        db.WorkflowVersions.Add(version);

        await db.SaveChangesAsync();

        return new Seed(
            ownerId,
            adminId,
            memberId,
            workspaceId,
            definitionId,
            versionId,
            now);
    }

    private static async Task<Guid> SeedEnabledTriggerAsync(
        string connectionString,
        Seed seed,
        DateTimeOffset dueAtUtc,
        string schedule)
    {
        var triggerId = Guid.NewGuid();

        await using var db = CreateContext(connectionString);
        var trigger = new WorkflowTrigger(
            triggerId,
            seed.WorkspaceId,
            seed.DefinitionId,
            seed.VersionId,
            schedule,
            "UTC",
            seed.AdminId,
            seed.AdminId,
            dueAtUtc,
            seed.Now.AddHours(-1));
        trigger.Enable(dueAtUtc);

        db.WorkflowTriggers.Add(trigger);
        await db.SaveChangesAsync();
        return triggerId;
    }

    private static User NewUser(
        Guid id,
        string name,
        DateTimeOffset now) =>
        new(
            id,
            $"{id:N}@scheduler.test",
            name,
            "not-a-real-hash",
            now);

    private sealed class FixedTimeProvider(
        DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<(string DatabaseName, string ConnectionString)>
        CreateIsolatedDatabaseAsync()
    {
        var source = new NpgsqlConnectionStringBuilder(Connection!);
        var databaseName =
            $"icehott_scheduler_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(Connection!)
        {
            Database = "postgres",
            Pooling = false
        };

        await using var connection =
            new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"CREATE DATABASE \"{databaseName}\"",
            connection);
        await command.ExecuteNonQueryAsync();

        source.Database = databaseName;
        source.Pooling = false;
        return (databaseName, source.ConnectionString);
    }

    private static async Task DropIsolatedDatabaseAsync(
        string databaseName)
    {
        var admin = new NpgsqlConnectionStringBuilder(Connection!)
        {
            Database = "postgres",
            Pooling = false
        };

        await using var connection =
            new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();

        await using (var terminate = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE datname = @name AND pid <> pg_backend_pid()",
            connection))
        {
            terminate.Parameters.AddWithValue(
                "name",
                databaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{databaseName}\"",
            connection);
        await drop.ExecuteNonQueryAsync();
    }

    private static ICEHOTTDbContext CreateContext(
        string connectionString) =>
        new(
            new DbContextOptionsBuilder<ICEHOTTDbContext>()
                .UseNpgsql(connectionString)
                .Options);
}
