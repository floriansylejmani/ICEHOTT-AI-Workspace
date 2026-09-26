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

public sealed class WorkflowPersistencePostgresTests
{
    private const string PreviousMigration = "20260925213117_Phase45AuditImmutability";

    private static string? Connection =>
        Environment.GetEnvironmentVariable("ICEHOTT_POSTGRES_TEST_CONNECTION");

    [Fact]
    public async Task Workflow_Migration_Audit_Trigger_Rolls_Back_And_Reapplies()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            await using var db = CreateContext(isolatedConnection);
            await db.Database.MigrateAsync();

            Assert.Equal(1, await CountWorkflowAuditTriggersAsync(db));
            Assert.True(await TableExistsAsync(db, "workflow_runs"));
            Assert.True(await TableExistsAsync(db, "workflow_trigger_fires"));

            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            Assert.Equal(0, await CountWorkflowAuditTriggersAsync(db));
            Assert.False(await TableExistsAsync(db, "workflow_runs"));

            await migrator.MigrateAsync();

            Assert.Equal(1, await CountWorkflowAuditTriggersAsync(db));
            Assert.True(await TableExistsAsync(db, "workflow_runs"));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Workflow_Audit_Is_AppendOnly_In_Postgres()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var world = await SeedWorldAsync(isolatedConnection);
            var auditId = Guid.NewGuid();

            await using (var db = CreateContext(isolatedConnection))
            {
                db.WorkflowAuditEvents.Add(new WorkflowAuditEvent(
                    auditId,
                    world.WorkspaceA,
                    WorkflowAuditEventType.RunRequested,
                    world.Now,
                    actorUserId: world.OwnerA,
                    detailJson: "{\"source\":\"test\"}"));
                await db.SaveChangesAsync();
            }

            await AssertAuditMutationRejectedAsync(
                isolatedConnection,
                $"UPDATE workflow_audit_events SET \"DetailJson\" = 'tampered' WHERE \"Id\" = '{auditId}'");

            await AssertAuditMutationRejectedAsync(
                isolatedConnection,
                $"DELETE FROM workflow_audit_events WHERE \"Id\" = '{auditId}'");

            await using var appendDb = CreateContext(isolatedConnection);
            appendDb.WorkflowAuditEvents.Add(new WorkflowAuditEvent(
                Guid.NewGuid(),
                world.WorkspaceA,
                WorkflowAuditEventType.StepStarted,
                world.Now.AddSeconds(1),
                actorUserId: world.OwnerA));

            await appendDb.SaveChangesAsync();
            Assert.Equal(
                2,
                await appendDb.WorkflowAuditEvents
                    .CountAsync(x => x.WorkspaceId == world.WorkspaceA));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task CrossWorkspace_Workflow_Version_Foreign_Key_Is_Rejected()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var world = await SeedWorldAsync(isolatedConnection);

            await using var db = CreateContext(isolatedConnection);
            db.WorkflowDefinitions.Add(new WorkflowDefinition(
                world.DefinitionA,
                world.WorkspaceA,
                "Tenant A workflow",
                null,
                WorkspaceRole.Member,
                world.OwnerA,
                world.Now));
            await db.SaveChangesAsync();

            db.WorkflowVersions.Add(new WorkflowVersion(
                Guid.NewGuid(),
                world.DefinitionA,
                world.WorkspaceB,
                1,
                "{\"steps\":[]}",
                Hash('a'),
                world.OwnerB,
                world.Now));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Only_One_Active_Version_Per_Definition_Is_Allowed()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var world = await SeedWorldAsync(isolatedConnection);
            await SeedDefinitionWithActiveVersionAsync(isolatedConnection, world);

            await using (var db = CreateContext(isolatedConnection))
            {
                var conflicting = new WorkflowVersion(
                    Guid.NewGuid(),
                    world.DefinitionA,
                    world.WorkspaceA,
                    2,
                    "{\"steps\":[{\"key\":\"two\"}]}",
                    Hash('b'),
                    world.OwnerA,
                    world.Now.AddMinutes(1));
                conflicting.Activate(world.Now.AddMinutes(1));
                db.WorkflowVersions.Add(conflicting);

                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                var active = await db.WorkflowVersions.SingleAsync(x =>
                    x.WorkflowDefinitionId == world.DefinitionA &&
                    x.Status == WorkflowVersionStatus.Active);
                active.Retire(world.Now.AddMinutes(2));
                await db.SaveChangesAsync();

                var replacement = new WorkflowVersion(
                    Guid.NewGuid(),
                    world.DefinitionA,
                    world.WorkspaceA,
                    2,
                    "{\"steps\":[{\"key\":\"two\"}]}",
                    Hash('c'),
                    world.OwnerA,
                    world.Now.AddMinutes(2));
                replacement.Activate(world.Now.AddMinutes(2));
                db.WorkflowVersions.Add(replacement);
                await db.SaveChangesAsync();
            }

            await using var verify = CreateContext(isolatedConnection);
            Assert.Equal(
                1,
                await verify.WorkflowVersions.CountAsync(x =>
                    x.WorkflowDefinitionId == world.DefinitionA &&
                    x.Status == WorkflowVersionStatus.Active));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Run_Idempotency_Is_Unique_Per_Workspace_And_Workflow()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var world = await SeedWorldAsync(isolatedConnection);
            var versionA = await SeedDefinitionWithActiveVersionAsync(
                isolatedConnection, world);

            var definitionB = Guid.NewGuid();
            var versionB = Guid.NewGuid();

            await using (var db = CreateContext(isolatedConnection))
            {
                db.WorkflowDefinitions.Add(new WorkflowDefinition(
                    definitionB,
                    world.WorkspaceB,
                    "Tenant B workflow",
                    null,
                    WorkspaceRole.Member,
                    world.OwnerB,
                    world.Now));

                var v = new WorkflowVersion(
                    versionB,
                    definitionB,
                    world.WorkspaceB,
                    1,
                    "{\"steps\":[]}",
                    Hash('d'),
                    world.OwnerB,
                    world.Now);
                v.Activate(world.Now);
                db.WorkflowVersions.Add(v);
                await db.SaveChangesAsync();
            }

            const string key = "same-idempotency-key";

            await using (var db = CreateContext(isolatedConnection))
            {
                db.WorkflowRuns.Add(NewRun(
                    world.WorkspaceA, world.DefinitionA, versionA,
                    world.OwnerA, key, world.Now));
                await db.SaveChangesAsync();
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                db.WorkflowRuns.Add(NewRun(
                    world.WorkspaceA, world.DefinitionA, versionA,
                    world.OwnerA, key, world.Now.AddSeconds(1)));

                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                db.WorkflowRuns.Add(NewRun(
                    world.WorkspaceB, definitionB, versionB,
                    world.OwnerB, key, world.Now.AddSeconds(2)));
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Trigger_Fire_Is_Deduplicated_By_Occurrence_And_FireKey()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var world = await SeedWorldAsync(isolatedConnection);
            var versionId = await SeedDefinitionWithActiveVersionAsync(
                isolatedConnection, world);
            var triggerId = Guid.NewGuid();
            var scheduled = world.Now.AddHours(1);

            await using (var db = CreateContext(isolatedConnection))
            {
                db.WorkflowTriggers.Add(new WorkflowTrigger(
                    triggerId,
                    world.WorkspaceA,
                    world.DefinitionA,
                    versionId,
                    "0 * * * *",
                    "UTC",
                    world.OwnerA,
                    world.OwnerA,
                    scheduled,
                    world.Now));
                await db.SaveChangesAsync();

                db.WorkflowTriggerFires.Add(new WorkflowTriggerFire(
                    Guid.NewGuid(),
                    world.WorkspaceA,
                    triggerId,
                    versionId,
                    scheduled,
                    "fire-1",
                    world.Now));
                await db.SaveChangesAsync();
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                db.WorkflowTriggerFires.Add(new WorkflowTriggerFire(
                    Guid.NewGuid(),
                    world.WorkspaceA,
                    triggerId,
                    versionId,
                    scheduled,
                    "fire-2",
                    world.Now.AddSeconds(1)));

                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                db.WorkflowTriggerFires.Add(new WorkflowTriggerFire(
                    Guid.NewGuid(),
                    world.WorkspaceA,
                    triggerId,
                    versionId,
                    scheduled.AddHours(1),
                    "fire-1",
                    world.Now.AddSeconds(2)));

                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }


    [Fact]
    public async Task Repositories_Enforce_Workspace_Scope_On_Reads()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            var world = await SeedWorldAsync(isolatedConnection);
            var definitionB = Guid.NewGuid();
            var artifactA = Guid.NewGuid();
            var artifactB = Guid.NewGuid();

            await using (var db = CreateContext(isolatedConnection))
            {
                var workflows = new WorkflowRepository(db);
                var artifacts = new ArtifactRepository(db);
                var audit = new WorkflowAuditRepository(db);

                await workflows.AddDefinitionAsync(new WorkflowDefinition(
                    world.DefinitionA, world.WorkspaceA, "A", null,
                    WorkspaceRole.Member, world.OwnerA, world.Now));
                await workflows.AddDefinitionAsync(new WorkflowDefinition(
                    definitionB, world.WorkspaceB, "B", null,
                    WorkspaceRole.Member, world.OwnerB, world.Now));

                await artifacts.AddAsync(new Artifact(
                    artifactA, world.WorkspaceA, world.OwnerA,
                    "a.txt", "text/plain", 1, Hash('a'), "a/key", world.Now));
                await artifacts.AddAsync(new Artifact(
                    artifactB, world.WorkspaceB, world.OwnerB,
                    "b.txt", "text/plain", 1, Hash('b'), "b/key", world.Now));

                await audit.AddAsync(new WorkflowAuditEvent(
                    Guid.NewGuid(), world.WorkspaceA,
                    WorkflowAuditEventType.DefinitionCreated, world.Now,
                    workflowDefinitionId: world.DefinitionA,
                    actorUserId: world.OwnerA));
                await audit.AddAsync(new WorkflowAuditEvent(
                    Guid.NewGuid(), world.WorkspaceB,
                    WorkflowAuditEventType.DefinitionCreated, world.Now,
                    workflowDefinitionId: definitionB,
                    actorUserId: world.OwnerB));

                await db.SaveChangesAsync();
            }

            await using var verify = CreateContext(isolatedConnection);
            var workflowRepo = new WorkflowRepository(verify);
            var artifactRepo = new ArtifactRepository(verify);
            var auditRepo = new WorkflowAuditRepository(verify);

            Assert.Null(await workflowRepo.FindDefinitionAsync(
                world.WorkspaceA, definitionB));
            Assert.Single(await workflowRepo.ListDefinitionsAsync(
                world.WorkspaceA, 20));

            Assert.Null(await artifactRepo.FindAsync(
                world.WorkspaceA, artifactB));
            var artifactsA = await artifactRepo.ListAsync(world.WorkspaceA, 20);
            Assert.Single(artifactsA);
            Assert.Equal(artifactA, artifactsA[0].Id);

            var auditA = await auditRepo.ListWorkspaceAsync(world.WorkspaceA, 20);
            Assert.Single(auditA);
            Assert.Equal(world.WorkspaceA, auditA[0].WorkspaceId);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    private sealed record World(
        Guid OwnerA,
        Guid OwnerB,
        Guid WorkspaceA,
        Guid WorkspaceB,
        Guid DefinitionA,
        DateTimeOffset Now);

    private static async Task<World> SeedWorldAsync(string connectionString)
    {
        await using var db = CreateContext(connectionString);
        await db.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();

        db.Users.AddRange(
            new User(
                ownerA,
                $"{ownerA:N}@workflow.test",
                "Owner A",
                "not-a-real-hash",
                now),
            new User(
                ownerB,
                $"{ownerB:N}@workflow.test",
                "Owner B",
                "not-a-real-hash",
                now));

        db.Workspaces.AddRange(
            new Workspace(
                workspaceA,
                "Workflow A",
                $"workflow-a-{workspaceA:N}",
                ownerA,
                now),
            new Workspace(
                workspaceB,
                "Workflow B",
                $"workflow-b-{workspaceB:N}",
                ownerB,
                now));

        db.WorkspaceMemberships.AddRange(
            new WorkspaceMembership(
                workspaceA,
                ownerA,
                WorkspaceRole.Owner,
                now),
            new WorkspaceMembership(
                workspaceB,
                ownerB,
                WorkspaceRole.Owner,
                now));

        await db.SaveChangesAsync();

        return new World(
            ownerA,
            ownerB,
            workspaceA,
            workspaceB,
            Guid.NewGuid(),
            now);
    }

    private static async Task<Guid> SeedDefinitionWithActiveVersionAsync(
        string connectionString,
        World world)
    {
        var versionId = Guid.NewGuid();

        await using var db = CreateContext(connectionString);
        db.WorkflowDefinitions.Add(new WorkflowDefinition(
            world.DefinitionA,
            world.WorkspaceA,
            "Tenant A workflow",
            null,
            WorkspaceRole.Member,
            world.OwnerA,
            world.Now));

        var version = new WorkflowVersion(
            versionId,
            world.DefinitionA,
            world.WorkspaceA,
            1,
            "{\"steps\":[]}",
            Hash('z'),
            world.OwnerA,
            world.Now);
        version.Activate(world.Now);
        db.WorkflowVersions.Add(version);

        await db.SaveChangesAsync();
        return versionId;
    }

    private static WorkflowRun NewRun(
        Guid workspaceId,
        Guid definitionId,
        Guid versionId,
        Guid userId,
        string key,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            workspaceId,
            definitionId,
            versionId,
            userId,
            userId,
            key,
            now);

    private static string Hash(char c) => new(c, 64);

    private static async Task AssertAuditMutationRejectedAsync(
        string connectionString,
        string sql)
    {
        await using var db = CreateContext(connectionString);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlRawAsync(sql));

        Assert.Equal("55000", exception.SqlState);
        Assert.Contains(
            "append-only",
            exception.MessageText,
            StringComparison.OrdinalIgnoreCase);
    }

    private static Task<int> CountWorkflowAuditTriggersAsync(ICEHOTTDbContext db) =>
        db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value"
            FROM pg_trigger
            WHERE NOT tgisinternal
              AND tgname = 'tr_workflow_audit_events_append_only'
            """).SingleAsync();

    private static Task<bool> TableExistsAsync(
        ICEHOTTDbContext db,
        string tableName) =>
        db.Database.SqlQueryRaw<bool>(
            """
            SELECT to_regclass({0}) IS NOT NULL AS "Value"
            """,
            $"public.{tableName}").SingleAsync();

    private static async Task<(string DatabaseName, string ConnectionString)>
        CreateIsolatedDatabaseAsync()
    {
        var source = new NpgsqlConnectionStringBuilder(Connection!);
        var databaseName = $"icehott_workflow_{Guid.NewGuid():N}";
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
