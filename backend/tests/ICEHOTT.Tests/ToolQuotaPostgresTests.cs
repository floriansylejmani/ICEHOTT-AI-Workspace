using System.Text.Json;
using ICEHOTT.Application.Tools;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Infrastructure.Tools;
using ICEHOTT.Persistence;
using ICEHOTT.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ICEHOTT.Tests;

/// <summary>
/// Phase 4.5 packet C against real PostgreSQL (runs only when
/// ICEHOTT_POSTGRES_TEST_CONNECTION is set): truly parallel admissions, each on
/// its own connection, never exceed the quota, exact retries never add a
/// charge, and tenants never share a counter.
/// </summary>
public sealed class ToolQuotaPostgresTests
{
    private const string Echo = "workspace.echo";

    private static string? Connection =>
        Environment.GetEnvironmentVariable("ICEHOTT_POSTGRES_TEST_CONNECTION");

    // Frozen mid-window so a window boundary can never fall inside a test.
    private static readonly TimeProvider Clock = new ManualTimeProvider(
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600 + 1800));

    [Fact]
    public async Task Postgres_Parallel_Distinct_Requests_Admit_Exactly_The_Limit()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        const int limit = 5;
        var world = await SeedAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(async () =>
        {
            await using var db = CreateContext();
            var result = await Service(db, limit).RequestAsync(
                world.Owner, world.WorkspaceId, Echo, Args($"parallel {i}"), $"pg-quota-{Guid.NewGuid():N}");
            return (result.ErrorCode, result.RetryAfterSeconds);
        })));

        Assert.Equal(limit, results.Count(x => x.ErrorCode is null));
        Assert.All(results.Where(x => x.ErrorCode is not null), x =>
        {
            Assert.Equal("tool_quota_exceeded", x.ErrorCode);
            Assert.InRange(x.RetryAfterSeconds ?? 0, 1, 3600);
        });

        await using var verify = CreateContext();
        Assert.Equal(limit, await verify.ToolExecutions.CountAsync(x => x.WorkspaceId == world.WorkspaceId));
        Assert.Equal(limit, await UsedAsync(verify, world.WorkspaceId, Echo));
    }

    [Fact]
    public async Task Postgres_Parallel_Identical_Retries_Create_One_Execution_And_One_Charge()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var world = await SeedAsync();
        var key = $"pg-same-{Guid.NewGuid():N}";

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            await using var db = CreateContext();
            var result = await Service(db, 5).RequestAsync(world.Owner, world.WorkspaceId, Echo, Args("same"), key);
            return (result.ErrorCode, Id: result.Value?.Id);
        })));

        Assert.All(results, x => Assert.Null(x.ErrorCode));
        Assert.Single(results.Select(x => x.Id).Distinct());

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.ToolExecutions.CountAsync(x => x.WorkspaceId == world.WorkspaceId));
        Assert.Equal(1, await UsedAsync(verify, world.WorkspaceId, Echo));
    }

    [Fact]
    public async Task Postgres_Parallel_Retries_Of_The_Execution_That_Took_The_Last_Permit_Are_Replays()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var world = await SeedAsync();
        var key = $"pg-last-{Guid.NewGuid():N}";

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            await using var db = CreateContext();
            var result = await Service(db, 1).RequestAsync(world.Owner, world.WorkspaceId, Echo, Args("only"), key);
            return (result.ErrorCode, Id: result.Value?.Id);
        })));

        Assert.All(results, x => Assert.Null(x.ErrorCode));
        Assert.Single(results.Select(x => x.Id).Distinct());

        await using var verify = CreateContext();
        Assert.Equal(1, await UsedAsync(verify, world.WorkspaceId, Echo));
    }

    [Fact]
    public async Task Postgres_Parallel_Requests_In_Two_Workspaces_Do_Not_Share_A_Quota()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        const int limit = 4;
        var a = await SeedAsync();
        var b = await SeedAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            var world = i % 2 == 0 ? a : b;
            await using var db = CreateContext();
            var result = await Service(db, limit).RequestAsync(
                world.Owner, world.WorkspaceId, Echo, Args($"tenant {i}"), $"pg-tenant-{Guid.NewGuid():N}");
            return (world.WorkspaceId, result.ErrorCode);
        })));

        Assert.Equal(limit, results.Count(x => x.WorkspaceId == a.WorkspaceId && x.ErrorCode is null));
        Assert.Equal(limit, results.Count(x => x.WorkspaceId == b.WorkspaceId && x.ErrorCode is null));

        await using var verify = CreateContext();
        Assert.Equal(limit, await UsedAsync(verify, a.WorkspaceId, Echo));
        Assert.Equal(limit, await UsedAsync(verify, b.WorkspaceId, Echo));
    }

    [Fact]
    public async Task Postgres_Quota_Counter_Constraints_Reject_Invalid_Rows()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var world = await SeedAsync();
        await using var db = CreateContext();

        var negative = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO tool_quota_counters ("WorkspaceId","ToolName","WindowStartUnixSeconds","Count")
            VALUES ({world.WorkspaceId}, 'x.tool', 0, -1)
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, negative.SqlState);

        var orphan = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO tool_quota_counters ("WorkspaceId","ToolName","WindowStartUnixSeconds","Count")
            VALUES ({Guid.NewGuid()}, 'x.tool', 0, 1)
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState);
    }

    private sealed record World(Guid WorkspaceId, Guid Owner);

    private static JsonElement Args(string text) => JsonSerializer.SerializeToElement(new { text });

    private static Task<int> UsedAsync(ICEHOTTDbContext db, Guid workspaceId, string toolName) =>
        db.ToolQuotaCounters
            .Where(x => x.WorkspaceId == workspaceId && x.ToolName == toolName)
            .Select(x => x.Count)
            .SingleOrDefaultAsync();

    private static async Task<World> SeedAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var owner = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users ("Id","Email","NormalizedEmail","DisplayName","PasswordHash","CreatedAtUtc")
            VALUES ({owner}, {owner + "@p45c.dev"}, {(owner + "@P45C.DEV").ToUpperInvariant()}, 'p45c', 'not-a-hash', {now})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workspaces ("Id","Name","Slug","CreatedByUserId","CreatedAtUtc")
            VALUES ({workspaceId}, 'P45C', {"p45c-" + workspaceId.ToString("N")}, {owner}, {now})
            """);
        db.WorkspaceMemberships.Add(new WorkspaceMembership(workspaceId, owner, WorkspaceRole.Owner, now));
        await db.SaveChangesAsync();

        // Materialise the policy row up front so parallel first uses contend on
        // the quota counter, not on the packet B version-0 policy anchor.
        var policy = await PolicyService(db).UpdateAsync(owner, workspaceId, Echo,
            JsonSerializer.SerializeToElement(new { expectedVersion = 0, enabled = true, requiresApproval = false }));
        Assert.Null(policy.ErrorCode);

        return new World(workspaceId, owner);
    }

    private static ToolRegistry Registry() => new([new WorkspaceEchoTool()]);

    private static ToolExecutionService Service(ICEHOTTDbContext db, int limit) =>
        new(
            new WorkspaceRepository(db),
            new ToolExecutionRepository(db),
            new ToolPolicyRepository(db),
            Registry(),
            Clock,
            new ToolQuotaOptions { DefaultPermitLimit = limit, WindowSeconds = 3600 },
            new ToolOperationalLog(NullLogger<ToolOperationalLog>.Instance));

    private static ToolPolicyService PolicyService(ICEHOTTDbContext db) =>
        new(new WorkspaceRepository(db), new ToolPolicyRepository(db), Registry(), TimeProvider.System);

    private static ICEHOTTDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ICEHOTTDbContext>()
            .UseNpgsql(Connection!)
            .Options);
}
