using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Tools;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using static ICEHOTT.Tests.ToolSecurityFixture;

namespace ICEHOTT.Tests;

/// <summary>
/// Phase 4.5 packet C: per-workspace, per-tool admission quotas
/// (docs/PHASE-4.5-C-BUDGETS-SECRETS.md C1-C4).
/// </summary>
public sealed class ToolQuotaTests(ToolBudgetFixture fx) : IClassFixture<ToolBudgetFixture>
{
    private const string Quota = ToolBudgetFixture.QuotaTool;
    private const int Limit = ToolBudgetFixture.QuotaToolLimit;
    private const string Echo = "workspace.echo";
    private const string Note = "workspace.audit-note.create";

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private int ExpectedRetryAfter()
    {
        var now = fx.Clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000d;
        var windowEnd = (Math.Floor(now / ToolBudgetFixture.WindowSeconds) + 1) * ToolBudgetFixture.WindowSeconds;
        return (int)Math.Max(1, Math.Ceiling(windowEnd - now));
    }

    private async Task ExhaustAsync(HttpClient client, Guid workspaceId, string tool = Quota)
    {
        for (var i = 0; i < Limit; i++)
        {
            var response = await RequestToolAsync(client, workspaceId, tool, new { text = $"fill {i}" }, NewKey("fill"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Admission_Beyond_The_Limit_Returns_429_With_Retry_Metadata()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);
        await ExhaustAsync(client, workspaceId);

        var rejected = await RequestToolAsync(client, workspaceId, Quota, new { text = "one too many" }, NewKey("over"));

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var body = await ReadJsonAsync(rejected);
        Assert.Equal("tool_quota_exceeded", body.GetProperty("code").GetString());
        var retryAfter = body.GetProperty("retryAfterSeconds").GetInt32();
        Assert.Equal(ExpectedRetryAfter(), retryAfter);
        Assert.Equal(TimeSpan.FromSeconds(retryAfter), rejected.Headers.RetryAfter?.Delta);

        Assert.Equal(Limit, await fx.CountExecutionsAsync(workspaceId));
        Assert.Equal(Limit, await fx.QuotaUsedAsync(workspaceId, Quota));
    }

    [Fact]
    public async Task Exact_Idempotent_Retries_Do_Not_Consume_Quota_Even_After_Exhaustion()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);
        var key = NewKey("retry");

        var first = await RequestToolAsync(client, workspaceId, Quota, new { text = "same" }, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstId = (await ReadJsonAsync(first)).GetProperty("id").GetGuid();

        for (var i = 0; i < 5; i++)
        {
            var retry = await RequestToolAsync(client, workspaceId, Quota, new { text = "same" }, key);
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(firstId, (await ReadJsonAsync(retry)).GetProperty("id").GetGuid());
        }

        Assert.Equal(1, await fx.QuotaUsedAsync(workspaceId, Quota));

        // The retries left the remaining permits untouched.
        for (var i = 1; i < Limit; i++)
            Assert.Equal(
                HttpStatusCode.OK,
                (await RequestToolAsync(client, workspaceId, Quota, new { text = $"new {i}" }, NewKey("new"))).StatusCode);

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await RequestToolAsync(client, workspaceId, Quota, new { text = "over" }, NewKey("over"))).StatusCode);

        // An exact retry of an admitted execution is still answered with that
        // execution while the quota is exhausted.
        var late = await RequestToolAsync(client, workspaceId, Quota, new { text = "same" }, key);
        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        Assert.Equal(firstId, (await ReadJsonAsync(late)).GetProperty("id").GetGuid());
        Assert.Equal(Limit, await fx.QuotaUsedAsync(workspaceId, Quota));
    }

    [Fact]
    public async Task Requests_That_Are_Not_Admitted_Do_Not_Consume_Quota()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Member, WorkspaceRole.Member));
        using var owner = fx.Client(fx.Owner);
        using var outsider = fx.Client(fx.Outsider);
        var key = NewKey("admitted");

        Assert.Equal(HttpStatusCode.OK,
            (await RequestToolAsync(owner, workspaceId, Quota, new { text = "a" }, key)).StatusCode);

        // Same key, different arguments.
        var conflict = await RequestToolAsync(owner, workspaceId, Quota, new { text = "b" }, key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        // Invalid arguments, invalid key, credential-shaped value, unknown tool, outsider.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await RequestToolAsync(owner, workspaceId, Quota, new { wrong = "x" }, NewKey())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await RequestToolAsync(owner, workspaceId, Quota, new { text = "x" }, "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await RequestToolAsync(owner, workspaceId, Quota, new { text = TestSecrets.GitHubToken }, NewKey())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await RequestToolAsync(owner, workspaceId, "no.such.tool", new { text = "x" }, NewKey())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await RequestToolAsync(outsider, workspaceId, Quota, new { text = "x" }, NewKey())).StatusCode);

        Assert.Equal(1, await fx.QuotaUsedAsync(workspaceId, Quota));
        Assert.Equal(1, await fx.CountExecutionsAsync(workspaceId));
    }

    [Fact]
    public async Task Quota_Is_Counted_Per_Tool()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);
        await ExhaustAsync(client, workspaceId);

        var echo = await RequestToolAsync(client, workspaceId, Echo, new { text = "other tool" }, NewKey("echo"));
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
        Assert.Equal(1, await fx.QuotaUsedAsync(workspaceId, Echo));
        Assert.Equal(Limit, await fx.QuotaUsedAsync(workspaceId, Quota));
    }

    [Fact]
    public async Task Quota_Is_Isolated_Per_Workspace_And_Not_Disclosed_To_Outsiders()
    {
        var workspaceA = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Member, WorkspaceRole.Member));
        var workspaceB = await fx.CreateWorkspaceAsync(fx.Admin);
        using var owner = fx.Client(fx.Owner);
        using var member = fx.Client(fx.Member);
        using var ownerB = fx.Client(fx.Admin);
        using var outsider = fx.Client(fx.Outsider);

        // The quota belongs to the workspace, not to the caller.
        await ExhaustAsync(owner, workspaceA);
        var memberOver = await RequestToolAsync(member, workspaceA, Quota, new { text = "member" }, NewKey());
        Assert.Equal(HttpStatusCode.TooManyRequests, memberOver.StatusCode);

        // Another tenant is unaffected.
        for (var i = 0; i < Limit; i++)
            Assert.Equal(HttpStatusCode.OK,
                (await RequestToolAsync(ownerB, workspaceB, Quota, new { text = $"b{i}" }, NewKey("b"))).StatusCode);

        // A non-member of the exhausted workspace learns nothing about its quota.
        var probe = await RequestToolAsync(outsider, workspaceA, Quota, new { text = "probe" }, NewKey("probe"));
        Assert.Equal(HttpStatusCode.NotFound, probe.StatusCode);
        Assert.Equal("workspace_not_found", await ReadErrorCodeAsync(probe));
        Assert.Null(probe.Headers.RetryAfter);

        // A forged workspace id in the body is ignored; the route decides.
        var forged = await member.PostAsJsonAsync(
            $"/api/workspaces/{workspaceA}/tool-executions",
            new { toolName = Quota, arguments = new { text = "forged" }, idempotencyKey = NewKey(), workspaceId = workspaceB });
        Assert.Equal(HttpStatusCode.TooManyRequests, forged.StatusCode);

        Assert.Equal(Limit, await fx.QuotaUsedAsync(workspaceA, Quota));
        Assert.Equal(Limit, await fx.QuotaUsedAsync(workspaceB, Quota));
    }

    [Fact]
    public async Task Quota_Window_Reopens_When_Retry_After_Has_Elapsed()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);
        await ExhaustAsync(client, workspaceId);

        var rejected = await RequestToolAsync(client, workspaceId, Quota, new { text = "wait" }, NewKey());
        var retryAfter = (await ReadJsonAsync(rejected)).GetProperty("retryAfterSeconds").GetInt32();
        Assert.InRange(retryAfter, 1, ToolBudgetFixture.WindowSeconds);

        fx.Clock.Advance(TimeSpan.FromSeconds(retryAfter) - TimeSpan.FromMilliseconds(1500));
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await RequestToolAsync(client, workspaceId, Quota, new { text = "early" }, NewKey())).StatusCode);

        fx.Clock.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(HttpStatusCode.OK,
            (await RequestToolAsync(client, workspaceId, Quota, new { text = "on time" }, NewKey())).StatusCode);
        Assert.Equal(1, await fx.QuotaUsedAsync(workspaceId, Quota));
    }

    [Fact]
    public async Task Approval_Tools_Are_Charged_At_Request_And_Approval_Is_Not_Blocked()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        using var admin = fx.Client(fx.Admin);
        using var owner = fx.Client(fx.Owner);

        var pending = new List<Guid>();
        for (var i = 0; i < ToolBudgetFixture.NoteLimit; i++)
        {
            var response = await RequestToolAsync(admin, workspaceId, Note, new { message = $"note {i}" }, NewKey("note"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            pending.Add((await ReadJsonAsync(response)).GetProperty("id").GetGuid());
        }

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await RequestToolAsync(admin, workspaceId, Note, new { message = "over" }, NewKey("note"))).StatusCode);

        foreach (var executionId in pending)
            Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(owner, workspaceId, executionId)).StatusCode);

        Assert.Equal(ToolBudgetFixture.NoteLimit, await fx.QuotaUsedAsync(workspaceId, Note));
    }

    [Fact]
    public async Task Losing_An_Idempotency_Insert_Race_Rolls_Back_The_Quota_Charge()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        var key = NewKey("race");

        Guid winnerId;
        using (var scope = fx.Factory.Services.CreateScope())
        {
            var winner = await Service(scope).RequestAsync(fx.Owner.UserId, workspaceId, Quota, Args(new { text = "race" }), key);
            Assert.Null(winner.ErrorCode);
            winnerId = winner.Value!.Id;
        }

        // The loser's pre-insert lookup missed the winner, so it charges the
        // quota and then collides on the unique idempotency index.
        using (var scope = fx.Factory.Services.CreateScope())
        {
            var loser = await Service(scope, missFirstLookup: true)
                .RequestAsync(fx.Owner.UserId, workspaceId, Quota, Args(new { text = "race" }), key);
            Assert.Null(loser.ErrorCode);
            Assert.Equal(winnerId, loser.Value!.Id);
        }

        Assert.Equal(1, await fx.QuotaUsedAsync(workspaceId, Quota));
        Assert.Equal(1, await fx.CountExecutionsAsync(workspaceId));
    }

    [Fact]
    public async Task Retry_Losing_To_Its_Own_Winner_At_The_Limit_Replays_Instead_Of_429()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);
        for (var i = 0; i < Limit - 1; i++)
            Assert.Equal(HttpStatusCode.OK,
                (await RequestToolAsync(client, workspaceId, Quota, new { text = $"f{i}" }, NewKey())).StatusCode);

        var key = NewKey("last");
        Guid winnerId;
        using (var scope = fx.Factory.Services.CreateScope())
        {
            var winner = await Service(scope).RequestAsync(fx.Owner.UserId, workspaceId, Quota, Args(new { text = "last" }), key);
            Assert.Null(winner.ErrorCode);
            winnerId = winner.Value!.Id;
        }

        using (var scope = fx.Factory.Services.CreateScope())
        {
            // Quota is now exhausted by the winner of this very key.
            var loser = await Service(scope, missFirstLookup: true)
                .RequestAsync(fx.Owner.UserId, workspaceId, Quota, Args(new { text = "last" }), key);
            Assert.Null(loser.ErrorCode);
            Assert.Equal(winnerId, loser.Value!.Id);
        }

        using (var scope = fx.Factory.Services.CreateScope())
        {
            var conflicting = await Service(scope, missFirstLookup: true)
                .RequestAsync(fx.Owner.UserId, workspaceId, Quota, Args(new { text = "different" }), key);
            Assert.Equal("idempotency_conflict", conflicting.ErrorCode);
        }

        Assert.Equal(Limit, await fx.QuotaUsedAsync(workspaceId, Quota));
    }

    [Fact]
    public void Quota_Options_Resolve_Per_Tool_Overrides_And_Reject_Invalid_Bounds()
    {
        var options = new ToolQuotaOptions
        {
            DefaultPermitLimit = 60,
            WindowSeconds = 60,
            Tools = new Dictionary<string, ToolQuotaLimit>
            {
                ["workspace.audit-note.create"] = new() { PermitLimit = 5, WindowSeconds = 3600 },
                ["workspace.echo"] = new() { PermitLimit = 10 }
            }
        };

        Assert.Empty(options.Validate());
        Assert.Equal(new ToolQuotaRule(5, 3600), options.Resolve("workspace.audit-note.create"));
        Assert.Equal(new ToolQuotaRule(5, 3600), options.Resolve("Workspace.Audit-Note.Create"));
        Assert.Equal(new ToolQuotaRule(10, 60), options.Resolve("workspace.echo"));
        Assert.Equal(new ToolQuotaRule(60, 60), options.Resolve("anything.else"));

        Assert.NotEmpty(new ToolQuotaOptions { DefaultPermitLimit = 0 }.Validate());
        Assert.NotEmpty(new ToolQuotaOptions { WindowSeconds = 0 }.Validate());
        Assert.NotEmpty(new ToolQuotaOptions { WindowSeconds = 86_401 }.Validate());
        Assert.NotEmpty(new ToolQuotaOptions { DefaultPermitLimit = 1_000_001 }.Validate());
        Assert.NotEmpty(new ToolQuotaOptions
        {
            Tools = new Dictionary<string, ToolQuotaLimit> { ["x"] = new() { PermitLimit = -1 } }
        }.Validate());
    }

    [Theory]
    [InlineData(1000.2, 60, 960, 20)]
    [InlineData(1020.0, 60, 1020, 60)]
    [InlineData(1079.999, 60, 1020, 1)]
    [InlineData(7199.5, 3600, 3600, 1)]
    public void Quota_Window_Is_Aligned_And_Retry_After_Rounds_Up(
        double unixSeconds, int window, long expectedStart, int expectedRetryAfter)
    {
        var rule = new ToolQuotaRule(10, window);
        var now = DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(unixSeconds * TimeSpan.TicksPerSecond));

        Assert.Equal(expectedStart, rule.WindowStart(now));
        Assert.Equal(expectedRetryAfter, rule.RetryAfterSeconds(now));
    }

    private ToolExecutionService Service(IServiceScope scope, bool missFirstLookup = false)
    {
        var provider = scope.ServiceProvider;
        IToolExecutionRepository repository = provider.GetRequiredService<IToolExecutionRepository>();
        if (missFirstLookup)
            repository = new MissFirstIdempotencyLookup(repository);

        return new ToolExecutionService(
            provider.GetRequiredService<IWorkspaceRepository>(),
            repository,
            provider.GetRequiredService<IToolPolicyRepository>(),
            provider.GetRequiredService<IToolRegistry>(),
            fx.Clock,
            provider.GetRequiredService<ToolQuotaOptions>(),
            provider.GetRequiredService<IToolOperationalLog>());
    }

    /// <summary>The first idempotency lookup misses (simulates the losing side of a race).</summary>
    internal sealed class MissFirstIdempotencyLookup(IToolExecutionRepository inner) : IToolExecutionRepository
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

        public Task<IReadOnlyList<ToolExecution>> ListExpiredRunningAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) =>
            inner.ListExpiredRunningAsync(now, limit, cancellationToken);

        public Task<IReadOnlyList<ToolExecutionAuditEvent>> ListAuditEventsAsync(
            Guid workspaceId, Guid executionId, CancellationToken cancellationToken = default) =>
            inner.ListAuditEventsAsync(workspaceId, executionId, cancellationToken);

        public Task AddAsync(ToolExecution execution, CancellationToken cancellationToken = default) =>
            inner.AddAsync(execution, cancellationToken);

        public Task AddAuditEventAsync(ToolExecutionAuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            inner.AddAuditEventAsync(auditEvent, cancellationToken);

        public Task<ToolPersistenceOutcome> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            inner.SaveChangesAsync(cancellationToken);

        public Task<ToolPersistenceOutcome> SaveAdmissionAsync(ToolQuotaCharge charge, CancellationToken cancellationToken = default) =>
            inner.SaveAdmissionAsync(charge, cancellationToken);

        public void DiscardPendingSideEffects(ToolExecution execution) =>
            inner.DiscardPendingSideEffects(execution);
    }
}
