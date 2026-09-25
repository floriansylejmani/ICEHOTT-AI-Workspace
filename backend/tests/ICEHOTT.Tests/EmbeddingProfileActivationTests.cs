using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Knowledge;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Tests;

public sealed class EmbeddingProfileActivationTests
{
    [Fact]
    public async Task Valid_Evidence_And_Coverage_Activates()
    {
        var setup = CreateSetup(indexReady: true, coverageComplete: true);
        await setup.Service.ActivateAsync(
            setup.Building.Id,
            setup.Evidence.Id);

        Assert.Equal(setup.Building.Id, setup.Store.ActivatedProfileId);
        Assert.Equal(setup.Evidence.Id, setup.Store.EvidenceId);
    }

    [Fact]
    public async Task Mismatched_Evidence_Is_Rejected()
    {
        var setup = CreateSetup(indexReady: true, coverageComplete: true);
        var wrongEvidence = CreateEvidence(
            Guid.NewGuid(),
            Guid.NewGuid(),
            setup.Building.Provider,
            setup.Building.Model,
            setup.Building.Dimensions,
            setup.Building.IndexVersion);

        setup.EvidenceRepository.Value = wrongEvidence;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => setup.Service.ActivateAsync(
                setup.Building.Id,
                wrongEvidence.Id));

        Assert.Null(setup.Store.ActivatedProfileId);
    }

    [Fact]
    public async Task Missing_Index_Is_Rejected()
    {
        var setup = CreateSetup(indexReady: false, coverageComplete: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => setup.Service.ActivateAsync(
                setup.Building.Id,
                setup.Evidence.Id));

        Assert.Null(setup.Store.ActivatedProfileId);
    }

    [Fact]
    public async Task Incomplete_Coverage_Is_Rejected()
    {
        var setup = CreateSetup(indexReady: true, coverageComplete: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => setup.Service.ActivateAsync(
                setup.Building.Id,
                setup.Evidence.Id));

        Assert.Null(setup.Store.ActivatedProfileId);
    }

    private static Setup CreateSetup(
        bool indexReady,
        bool coverageComplete)
    {
        var now = DateTimeOffset.UtcNow;
        var active = new EmbeddingProfile(
            Guid.NewGuid(), "active", "local", "active-model", 64,
            "1", 1, "cosine", "unit",
            EmbeddingProfileStatus.Active, now);

        var building = new EmbeddingProfile(
            Guid.NewGuid(), "building", "provider", "model", 128,
            "1", 2, "cosine", "unit",
            EmbeddingProfileStatus.Building, now);
        var evidence = CreateEvidence(
            Guid.NewGuid(),
            building.Id,
            building.Provider,
            building.Model,
            building.Dimensions,
            building.IndexVersion);

        var profiles = new FakeProfiles(active, building);
        var evidenceRepository = new FakeEvidenceRepository(evidence);
        var indexes = new FakeIndexes(indexReady);
        var coverage = new FakeCoverage(
            coverageComplete
                ? new EmbeddingProfileCoverage(3, 3)
                : new EmbeddingProfileCoverage(3, 2));
        var store = new FakeActivationStore();

        var service = new EmbeddingProfileActivationService(
            profiles,
            evidenceRepository,
            indexes,
            coverage,
            store,
            RagEvaluationPromotionPolicy.StrictV1,
            TimeProvider.System);
        return new Setup(
            service,
            building,
            evidence,
            evidenceRepository,
            store);
    }

    private static RagEvaluationEvidence CreateEvidence(
        Guid id,
        Guid profileId,
        string provider,
        string model,
        int dimensions,
        int indexVersion) =>
        new(
            id,
            "rag-v1",
            profileId,
            provider,
            model,
            dimensions,
            indexVersion,
            1.0,
            1.0,
            1.0,
            1.0,
            0,
            DateTimeOffset.UtcNow,
            RagEvaluationKind.Deterministic,
            "test-runner-v1");
    private sealed record Setup(
        EmbeddingProfileActivationService Service,
        EmbeddingProfile Building,
        RagEvaluationEvidence Evidence,
        FakeEvidenceRepository EvidenceRepository,
        FakeActivationStore Store);

    private sealed class FakeProfiles(
        EmbeddingProfile active,
        EmbeddingProfile building)
        : IEmbeddingProfileRepository
    {
        public Task<EmbeddingProfile?> GetActiveAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EmbeddingProfile?>(active);

        public Task<EmbeddingProfile?> GetBuildingAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EmbeddingProfile?>(building);

        public Task<EmbeddingProfile?> FindAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EmbeddingProfile?>(
                id == building.Id ? building : id == active.Id ? active : null);
        public Task AddAsync(
            EmbeddingProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeEvidenceRepository(
        RagEvaluationEvidence evidence)
        : IRagEvaluationEvidenceRepository
    {
        public RagEvaluationEvidence Value { get; set; } = evidence;

        public Task<RagEvaluationEvidence?> FindAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RagEvaluationEvidence?>(
                id == Value.Id ? Value : null);

        public Task AddAsync(
            RagEvaluationEvidence value,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
    private sealed class FakeIndexes(bool ready)
        : IVectorIndexProvisioner
    {
        public Task EnsureBuildIndexAsync(
            EmbeddingProfileDescriptor profile,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> IsIndexReadyAsync(
            EmbeddingProfileDescriptor profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ready);

        public Task DropRetiredIndexAsync(
            EmbeddingProfileDescriptor profile,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeCoverage(EmbeddingProfileCoverage value)
        : IEmbeddingProfileCoverageService
    {
        public Task<EmbeddingProfileCoverage> GetCoverageAsync(
            EmbeddingProfileDescriptor profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(value);
    }

    private sealed class FakeActivationStore : IEmbeddingProfileActivationStore
    {
        public Guid? ActivatedProfileId { get; private set; }
        public Guid? EvidenceId { get; private set; }

        public Task ActivateAsync(
            Guid buildingProfileId,
            Guid evidenceId,
            DateTimeOffset activatedAtUtc,
            CancellationToken cancellationToken = default)
        {
            ActivatedProfileId = buildingProfileId;
            EvidenceId = evidenceId;
            return Task.CompletedTask;
        }
    }
}
