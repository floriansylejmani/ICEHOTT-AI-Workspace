using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Knowledge;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Tests;

/// <summary>
/// Proves that the serving resolver only returns Active profiles and the build
/// resolver only returns Building profiles — the core B1 merge gate.
/// </summary>
public sealed class EmbeddingProfileResolverTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static EmbeddingProfile MakeProfile(EmbeddingProfileStatus status) =>
        new(
            Guid.NewGuid(),
            $"test-{status.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
            "test-provider",
            "test-model",
            64,
            "1",
            1,
            "cosine",
            "unit",
            status,
            DateTimeOffset.UtcNow);

    // ── ServingEmbeddingProfileResolver ─────────────────────────────────────

    [Fact]
    public async Task Serving_Resolver_Returns_Active_Profile()
    {
        var active = MakeProfile(EmbeddingProfileStatus.Active);
        var resolver = new ServingEmbeddingProfileResolver(
            new StubRepository(activeResult: active));

        var result = await resolver.ResolveAsync();

        Assert.Equal(active.Id, result.Id);
        Assert.Equal(active.Provider, result.Provider);
        Assert.Equal(active.Dimensions, result.Dimensions);
    }

    [Fact]
    public async Task Serving_Resolver_Throws_When_No_Active_Profile_Exists()
    {
        var resolver = new ServingEmbeddingProfileResolver(
            new StubRepository(activeResult: null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync());
    }

    [Theory]
    [InlineData(EmbeddingProfileStatus.Building)]
    [InlineData(EmbeddingProfileStatus.Retired)]
    [InlineData(EmbeddingProfileStatus.Failed)]
    public async Task Serving_Resolver_Rejects_NonActive_Profile_From_Repository(
        EmbeddingProfileStatus wrongStatus)
    {
        // Defensive invariant: even if the repository returns a wrong-status profile,
        // the serving resolver must refuse to serve from it.
        var wrongProfile = MakeProfile(wrongStatus);
        var resolver = new ServingEmbeddingProfileResolver(
            new StubRepository(activeResult: wrongProfile));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync());
    }

    // ── BuildEmbeddingProfileResolver ───────────────────────────────────────

    [Fact]
    public async Task Build_Resolver_Returns_Building_Profile()
    {
        var building = MakeProfile(EmbeddingProfileStatus.Building);
        var resolver = new BuildEmbeddingProfileResolver(
            new StubRepository(buildingResult: building));

        var result = await resolver.ResolveAsync();

        Assert.Equal(building.Id, result.Id);
        Assert.Equal(building.Provider, result.Provider);
        Assert.Equal(building.Dimensions, result.Dimensions);
    }

    [Fact]
    public async Task Build_Resolver_Throws_When_No_Building_Profile_Exists()
    {
        var resolver = new BuildEmbeddingProfileResolver(
            new StubRepository(buildingResult: null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync());
    }

    [Theory]
    [InlineData(EmbeddingProfileStatus.Active)]
    [InlineData(EmbeddingProfileStatus.Retired)]
    [InlineData(EmbeddingProfileStatus.Failed)]
    public async Task Build_Resolver_Rejects_NonBuilding_Profile_From_Repository(
        EmbeddingProfileStatus wrongStatus)
    {
        // Defensive invariant: even if the repository returns a wrong-status profile,
        // the build resolver must refuse to drive build operations with it.
        var wrongProfile = MakeProfile(wrongStatus);
        var resolver = new BuildEmbeddingProfileResolver(
            new StubRepository(buildingResult: wrongProfile));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync());
    }

    // ── Stub ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Repository stub that returns caller-supplied profiles verbatim,
    /// allowing tests to inject profiles with arbitrary (including wrong) status values.
    /// </summary>
    private sealed class StubRepository(
        EmbeddingProfile? activeResult = null,
        EmbeddingProfile? buildingResult = null)
        : IEmbeddingProfileRepository
    {
        public Task<EmbeddingProfile?> GetActiveAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(activeResult);

        public Task<EmbeddingProfile?> GetBuildingAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(buildingResult);

        public Task<EmbeddingProfile?> FindAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EmbeddingProfile?>(null);

        public Task AddAsync(
            EmbeddingProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
