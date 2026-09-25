using System.Text.Json;
using ICEHOTT.Application.Tools;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Infrastructure.Tools;
using ICEHOTT.Persistence;
using ICEHOTT.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ICEHOTT.Tests;

/// <summary>
/// Phase 4.5 packet E: PostgreSQL-enforced append-only tool audit rows and
/// migration rollback/reapply proof. Tests are skipped when no PG connection
/// is configured, matching the other PostgreSQL integration suites.
/// </summary>
public sealed class ToolAuditPostgresTests
{
    private const string Echo = "workspace.echo";
    private const string Note = "workspace.audit-note.create";
    private const string PreviousMigration = "20260925204435_Phase45ToolQuotas";

    private static string? Connection =>
        Environment.GetEnvironmentVariable("ICEHOTT_POSTGRES_TEST_CONNECTION");
    [Fact]
    public async Task Postgres_Tool_Audit_Rows_Reject_Update_And_Delete_But_Allow_Appends()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var world = await SeedAsync();

        Guid executionAuditId;
        Guid policyAuditId;
        await using (var db = CreateContext(Connection!))
        {
            var echo = await ExecutionService(db).RequestAsync(
                world.Owner,
                world.WorkspaceId,
                Echo,
                JsonSerializer.SerializeToElement(new { text = "audit append" }),
                $"audit-{Guid.NewGuid():N}");
            Assert.Null(echo.ErrorCode);

            var policy = await PolicyService(db).UpdateAsync(
                world.Owner,
                world.WorkspaceId,
                Note,
                JsonSerializer.SerializeToElement(new
                {
                    expectedVersion = 0,
                    enabled = true,
                    requiresApproval = true
                }));
            Assert.Null(policy.ErrorCode);

            executionAuditId = await db.ToolExecutionAuditEvents
                .Where(x => x.ExecutionId == echo.Value!.Id)
                .Select(x => x.Id)
                .FirstAsync();
            policyAuditId = await db.ToolPolicyAuditEvents
                .Where(x => x.WorkspaceId == world.WorkspaceId && x.ToolName == Note)
                .Select(x => x.Id)
                .SingleAsync();
        }
        await AssertAuditMutationRejectedAsync(
            $"UPDATE tool_execution_audit_events SET \"OccurredAtUtc\" = \"OccurredAtUtc\" WHERE \"Id\" = '{executionAuditId}'");
        await AssertAuditMutationRejectedAsync(
            $"DELETE FROM tool_execution_audit_events WHERE \"Id\" = '{executionAuditId}'");
        await AssertAuditMutationRejectedAsync(
            $"UPDATE tool_policy_audit_events SET \"OccurredAtUtc\" = \"OccurredAtUtc\" WHERE \"Id\" = '{policyAuditId}'");
        await AssertAuditMutationRejectedAsync(
            $"DELETE FROM tool_policy_audit_events WHERE \"Id\" = '{policyAuditId}'");

        await using var appendDb = CreateContext(Connection!);
        var secondEcho = await ExecutionService(appendDb).RequestAsync(
            world.Owner,
            world.WorkspaceId,
            Echo,
            JsonSerializer.SerializeToElement(new { text = "second append" }),
            $"audit-{Guid.NewGuid():N}");
        Assert.Null(secondEcho.ErrorCode);

        var secondPolicy = await PolicyService(appendDb).UpdateAsync(
            world.Owner,
            world.WorkspaceId,
            Note,
            JsonSerializer.SerializeToElement(new
            {
                expectedVersion = 1,
                enabled = false,
                requiresApproval = true
            }));
        Assert.Null(secondPolicy.ErrorCode);
        Assert.True(await appendDb.ToolExecutionAuditEvents
            .AnyAsync(x => x.ExecutionId == secondEcho.Value!.Id));
        Assert.Equal(2, await appendDb.ToolPolicyAuditEvents
            .CountAsync(x => x.WorkspaceId == world.WorkspaceId && x.ToolName == Note));
    }
    [Fact]
    public async Task Postgres_Audit_Immutability_Migration_Rolls_Back_And_Reapplies()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            await using var db = CreateContext(isolatedConnection);
            await db.Database.MigrateAsync();
            Assert.Equal(2, await CountAuditTriggersAsync(db));

            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            Assert.Equal(0, await CountAuditTriggersAsync(db));

            await migrator.MigrateAsync();
            Assert.Equal(2, await CountAuditTriggersAsync(db));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    private sealed record World(Guid WorkspaceId, Guid Owner);

    private static async Task<World> SeedAsync()
    {
        await using var db = CreateContext(Connection!);
        await db.Database.MigrateAsync();

        var owner = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users ("Id","Email","NormalizedEmail","DisplayName","PasswordHash","CreatedAtUtc")
            VALUES ({owner}, {owner + "@p45e.dev"}, {(owner + "@P45E.DEV").ToUpperInvariant()},
                    'p45e', 'not-a-hash', {now})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workspaces ("Id","Name","Slug","CreatedByUserId","CreatedAtUtc")
            VALUES ({workspaceId}, 'P45E', {"p45e-" + workspaceId.ToString("N")}, {owner}, {now})
            """);
        db.WorkspaceMemberships.Add(
            new WorkspaceMembership(workspaceId, owner, WorkspaceRole.Owner, now));
        await db.SaveChangesAsync();

        return new World(workspaceId, owner);
    }

    private static async Task AssertAuditMutationRejectedAsync(string sql)
    {
        await using var db = CreateContext(Connection!);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal("55000", exception.SqlState);
        Assert.Contains("append-only", exception.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<int> CountAuditTriggersAsync(ICEHOTTDbContext db) =>
        db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value"
            FROM pg_trigger
            WHERE NOT tgisinternal
              AND tgname IN ('tr_tool_execution_audit_events_append_only',
                             'tr_tool_policy_audit_events_append_only')
            """).SingleAsync();
    private static async Task<(string DatabaseName, string ConnectionString)> CreateIsolatedDatabaseAsync()
    {
        var source = new NpgsqlConnectionStringBuilder(Connection!);
        var databaseName = $"icehott_audit_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(Connection!)
        {
            Database = "postgres",
            Pooling = false
        };

        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"CREATE DATABASE \"{databaseName}\"", connection);
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

        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{databaseName}\"", connection);
        await drop.ExecuteNonQueryAsync();
    }
    private static ToolRegistry Registry(ICEHOTTDbContext db) =>
        new([
            new WorkspaceEchoTool(),
            new WorkspaceAuditNoteCreateTool(
                new WorkspaceAuditNoteRepository(db),
                TimeProvider.System)
        ]);

    private static ToolExecutionService ExecutionService(ICEHOTTDbContext db) =>
        new(
            new WorkspaceRepository(db),
            new ToolExecutionRepository(db),
            new ToolPolicyRepository(db),
            Registry(db),
            TimeProvider.System,
            new ToolQuotaOptions(),
            new ToolOperationalLog(
                NullLogger<ToolOperationalLog>.Instance));

    private static ToolPolicyService PolicyService(ICEHOTTDbContext db) =>
        new(
            new WorkspaceRepository(db),
            new ToolPolicyRepository(db),
            Registry(db),
            TimeProvider.System);

    private static ICEHOTTDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ICEHOTTDbContext>()
            .UseNpgsql(connectionString)
            .Options);
}
