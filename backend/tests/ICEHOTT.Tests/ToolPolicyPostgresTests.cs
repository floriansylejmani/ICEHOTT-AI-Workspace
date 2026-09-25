using System.Text.Json;
using ICEHOTT.Application.Tools;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Infrastructure.Tools;
using ICEHOTT.Persistence;
using ICEHOTT.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Tests;

/// <summary>
/// Phase 4.5 packet B against real PostgreSQL (runs only when
/// ICEHOTT_POSTGRES_TEST_CONNECTION is set, like the Phase 3.6B PG tests):
/// migrations apply, and truly concurrent approvals / policy updates /
/// approval-vs-disable races resolve to at most one side effect.
/// </summary>
public sealed class ToolPolicyPostgresTests
{
    private const string Note = "workspace.audit-note.create";

    private static string? Connection =>
        Environment.GetEnvironmentVariable("ICEHOTT_POSTGRES_TEST_CONNECTION");

    [Fact]
    public async Task Postgres_Concurrent_Approvals_Produce_Exactly_One_Side_Effect()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var world = await SeedAsync();
        var executionId = await RequestPendingNoteAsync(world);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
            {
                await using var db = CreateContext();
                var approver = i % 2 == 0 ? world.Owner : world.Admin2;
                return (await ExecutionService(db).ApproveAsync(approver, world.WorkspaceId, executionId)).ErrorCode;
            })));

        // Losers either saw a non-pending state / lost the status CAS, or lost
        // the version-0 policy anchor insert (no overlay exists yet). Both
        // fail closed; exactly one approval wins.
        Assert.Single(results, code => code is null);
        Assert.All(
            results.Where(code => code is not null),
            code => Assert.Contains(code, new[] { "invalid_state", "policy_changed" }));

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.WorkspaceAuditNotes.CountAsync(x => x.ToolExecutionId == executionId));
        Assert.Equal(1, await verify.ToolExecutionAuditEvents.CountAsync(
            x => x.ExecutionId == executionId && x.EventType == ToolExecutionAuditEventType.Approved));
    }

    [Fact]
    public async Task Postgres_Concurrent_Policy_Updates_Commit_Exactly_One_Version()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var world = await SeedAsync();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
            {
                await using var db = CreateContext();
                var body = JsonSerializer.SerializeToElement(new
                {
                    expectedVersion = 0,
                    enabled = i % 2 == 0,
                    requiresApproval = true,
                    maxArgumentLength = 100 + i
                });
                return (await PolicyService(db).UpdateAsync(world.Owner, world.WorkspaceId, Note, body)).ErrorCode;
            })));

        Assert.Single(results, code => code is null);
        Assert.All(results.Where(code => code is not null), code => Assert.Equal("policy_version_conflict", code));

        await using var verify = CreateContext();
        var policy = await verify.ToolPolicies.SingleAsync(x => x.WorkspaceId == world.WorkspaceId && x.ToolName == Note);
        Assert.Equal(1, policy.Version);
        Assert.Equal(1, await verify.ToolPolicyAuditEvents.CountAsync(x => x.WorkspaceId == world.WorkspaceId));
    }

    [Fact]
    public async Task Postgres_Approval_Racing_Disable_Never_Runs_Under_Obsolete_Policy()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;

        for (var round = 0; round < 10; round++)
        {
            var world = await SeedAsync();
            // Start from an explicit overlay so both sides contend on the row.
            await using (var db = CreateContext())
                Assert.Null((await PolicyService(db).UpdateAsync(world.Owner, world.WorkspaceId, Note,
                    JsonSerializer.SerializeToElement(new { expectedVersion = 0, enabled = true, requiresApproval = true }))).ErrorCode);

            var executionId = await RequestPendingNoteAsync(world);

            var approve = Task.Run(async () =>
            {
                await using var db = CreateContext();
                return (await ExecutionService(db).ApproveAsync(world.Admin2, world.WorkspaceId, executionId)).ErrorCode;
            });
            var disable = Task.Run(async () =>
            {
                await using var db = CreateContext();
                return (await PolicyService(db).UpdateAsync(world.Owner, world.WorkspaceId, Note,
                    JsonSerializer.SerializeToElement(new { expectedVersion = 1, enabled = false, requiresApproval = true }))).ErrorCode;
            });

            var (approveCode, disableCode) = (await approve, await disable);

            await using var verify = CreateContext();
            var execution = await verify.ToolExecutions.SingleAsync(x => x.Id == executionId);
            var notes = await verify.WorkspaceAuditNotes.CountAsync(x => x.ToolExecutionId == executionId);
            var policy = await verify.ToolPolicies.SingleAsync(x => x.WorkspaceId == world.WorkspaceId && x.ToolName == Note);

            if (approveCode is null)
            {
                // Approval linearised first: it ran exactly once under version 1.
                Assert.Equal(ToolExecutionStatus.Succeeded, execution.Status);
                Assert.Equal(1, notes);
            }
            else
            {
                Assert.Contains(approveCode, new[] { "policy_changed", "tool_disabled" });
                Assert.Equal(ToolExecutionStatus.PendingApproval, execution.Status);
                Assert.Equal(0, notes);
            }

            // Either the disable committed (version 2) or it lost to the
            // approval's version assertion and must be retried; never both lost.
            Assert.True(disableCode is null || approveCode is null);
            Assert.Equal(disableCode is null ? 2 : 1, policy.Version);
        }
    }

    private sealed record World(Guid WorkspaceId, Guid Owner, Guid Admin, Guid Admin2);

    private static async Task<World> SeedAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var owner = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var admin2 = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        foreach (var userId in new[] { owner, admin, admin2 })
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users ("Id","Email","NormalizedEmail","DisplayName","PasswordHash","CreatedAtUtc")
                VALUES ({userId}, {userId + "@p45.dev"}, {(userId + "@P45.DEV").ToUpperInvariant()}, 'p45', 'not-a-hash', {now})
                """);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workspaces ("Id","Name","Slug","CreatedByUserId","CreatedAtUtc")
            VALUES ({workspaceId}, 'P45', {"p45-" + workspaceId.ToString("N")}, {owner}, {now})
            """);

        db.WorkspaceMemberships.AddRange(
            new WorkspaceMembership(workspaceId, owner, WorkspaceRole.Owner, now),
            new WorkspaceMembership(workspaceId, admin, WorkspaceRole.Admin, now),
            new WorkspaceMembership(workspaceId, admin2, WorkspaceRole.Admin, now));
        await db.SaveChangesAsync();

        return new World(workspaceId, owner, admin, admin2);
    }

    private static async Task<Guid> RequestPendingNoteAsync(World world)
    {
        await using var db = CreateContext();
        var result = await ExecutionService(db).RequestAsync(
            world.Admin,
            world.WorkspaceId,
            Note,
            JsonSerializer.SerializeToElement(new { message = "pg race" }),
            $"pg-{Guid.NewGuid():N}");
        Assert.Null(result.ErrorCode);
        return result.Value!.Id;
    }

    private static ToolRegistry Registry(ICEHOTTDbContext db) =>
        new([
            new WorkspaceEchoTool(),
            new WorkspaceAuditNoteCreateTool(new WorkspaceAuditNoteRepository(db), TimeProvider.System)
        ]);

    private static ToolExecutionService ExecutionService(ICEHOTTDbContext db) =>
        new(
            new WorkspaceRepository(db),
            new ToolExecutionRepository(db),
            new ToolPolicyRepository(db),
            Registry(db),
            TimeProvider.System);

    private static ToolPolicyService PolicyService(ICEHOTTDbContext db) =>
        new(
            new WorkspaceRepository(db),
            new ToolPolicyRepository(db),
            Registry(db),
            TimeProvider.System);

    private static ICEHOTTDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ICEHOTTDbContext>()
            .UseNpgsql(Connection!)
            .Options);
}
