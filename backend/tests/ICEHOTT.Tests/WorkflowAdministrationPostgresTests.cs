using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using ICEHOTT.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ICEHOTT.Tests;

public sealed class WorkflowAdministrationPostgresTests
{
    private static string? Connection =>
        Environment.GetEnvironmentVariable("ICEHOTT_POSTGRES_TEST_CONNECTION");

    [Fact]
    public async Task Concurrent_Run_Request_Is_Idempotent_In_Postgres()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, connectionString) = await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedAsync(connectionString);
            var key = $"race-{Guid.NewGuid():N}";

            await using var db1 = CreateContext(connectionString);
            await using var db2 = CreateContext(connectionString);
            var store1 = new WorkflowAdministrationStore(db1);
            var store2 = new WorkflowAdministrationStore(db2);

            var results = await Task.WhenAll(
                store1.CreateRunAsync(
                    seed.OwnerId, seed.WorkspaceId, seed.DefinitionId,
                    key, seed.Now.AddMinutes(1)),
                store2.CreateRunAsync(
                    seed.OwnerId, seed.WorkspaceId, seed.DefinitionId,
                    key, seed.Now.AddMinutes(1)));

            Assert.Single(results,
                x => x.Outcome == WorkflowAdministrationPersistenceOutcome.Saved);
            Assert.Single(results,
                x => x.Outcome == WorkflowAdministrationPersistenceOutcome.IdempotentReplay);
            Assert.Equal(results[0].Run!.Id, results[1].Run!.Id);

            await using var verify = CreateContext(connectionString);
            Assert.Equal(1, await verify.WorkflowRuns.CountAsync(
                x => x.WorkspaceId == seed.WorkspaceId &&
                     x.WorkflowDefinitionId == seed.DefinitionId &&
                     x.IdempotencyKey == key));
            Assert.Equal(1, await verify.WorkflowAuditEvents.CountAsync(
                x => x.WorkflowRunId == results[0].Run!.Id &&
                     x.EventType == WorkflowAuditEventType.RunRequested));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Run_Request_Revalidates_Current_Minimum_Role()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, connectionString) = await CreateIsolatedDatabaseAsync();

        try
        {
            var seed = await SeedAsync(connectionString, WorkspaceRole.Admin);

            await using (var db = CreateContext(connectionString))
            {
                var store = new WorkflowAdministrationStore(db);
                var denied = await store.CreateRunAsync(
                    seed.MemberId, seed.WorkspaceId, seed.DefinitionId,
                    $"denied-{Guid.NewGuid():N}", seed.Now.AddMinutes(1));

                Assert.Equal(
                    WorkflowAdministrationPersistenceOutcome.RunRoleNotAuthorized,
                    denied.Outcome);
            }

            await using var verify = CreateContext(connectionString);
            Assert.Empty(await verify.WorkflowRuns
                .Where(x => x.RequestedByUserId == seed.MemberId)
                .ToListAsync());
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    private static async Task<Seed> SeedAsync(
        string connectionString,
        WorkspaceRole minimumRunRole = WorkspaceRole.Member)
    {
        await using var db = CreateContext(connectionString);
        await db.Database.MigrateAsync();

        var now = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        var ownerId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        db.Users.AddRange(
            NewUser(ownerId, "Owner", now),
            NewUser(memberId, "Member", now));
        db.Workspaces.Add(new Workspace(
            workspaceId, "Administration Workspace",
            $"workflow-admin-{workspaceId:N}", ownerId, now));
        db.WorkspaceMemberships.AddRange(
            new WorkspaceMembership(workspaceId, ownerId, WorkspaceRole.Owner, now),
            new WorkspaceMembership(workspaceId, memberId, WorkspaceRole.Member, now));

        var definition = new WorkflowDefinition(
            definitionId, workspaceId, "Administration workflow", null,
            minimumRunRole, ownerId, now);
        definition.MarkActive(now);
        db.WorkflowDefinitions.Add(definition);

        const string definitionJson =
            """{"steps":[{"key":"delay","type":"delay","delaySeconds":0}]}""";
        var version = new WorkflowVersion(
            versionId, definitionId, workspaceId, 1, definitionJson,
            new string('a', 64), ownerId, now);
        version.Activate(now);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync();

        return new(ownerId, memberId, workspaceId, definitionId, now);
    }

    private static User NewUser(Guid id, string name, DateTimeOffset now) =>
        new(id, $"{id:N}@workflow-admin.test", name, "not-a-real-hash", now);

    private static ICEHOTTDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ICEHOTTDbContext>()
            .UseNpgsql(connectionString).Options);

    private static async Task<(string DatabaseName, string ConnectionString)>
        CreateIsolatedDatabaseAsync()
    {
        var source = new NpgsqlConnectionStringBuilder(Connection!);
        var databaseName = $"icehott_workflow_admin_{Guid.NewGuid():N}";
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

    private sealed record Seed(
        Guid OwnerId,
        Guid MemberId,
        Guid WorkspaceId,
        Guid DefinitionId,
        DateTimeOffset Now);
}
