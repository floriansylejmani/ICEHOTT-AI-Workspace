using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Knowledge;
using ICEHOTT.Domain.Knowledge;
using ICEHOTT.Persistence;
using ICEHOTT.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Tests;

public sealed class Phase36BPostgresIntegrationTests
{
    private static readonly Guid BaselineProfileId =
        EmbeddingProfileDefaults.LocalDeterministic64Id;

    [Fact]
    public async Task Postgres_Profile_Index_And_Activation_Swap_Are_Atomic()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ICEHOTT_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var db = CreateContext(connectionString);
        await db.Database.MigrateAsync();

        var baseline = await db.EmbeddingProfiles
            .SingleAsync(x => x.Id == BaselineProfileId);
        Assert.Equal(EmbeddingProfileStatus.Active, baseline.Status);

        var candidateId = Guid.NewGuid();
        var candidate = CreateBuildingProfile(candidateId);
        db.EmbeddingProfiles.Add(candidate);
        await db.SaveChangesAsync();

        var descriptor = ToDescriptor(candidate);
        var provisioner = new PostgresVectorIndexProvisioner(db);

        try
        {
            await provisioner.EnsureBuildIndexAsync(descriptor);
            Assert.True(await provisioner.IsIndexReadyAsync(descriptor));

            var evidence = CreatePassingEvidence(candidateId, descriptor);
            db.RagEvaluationEvidence.Add(evidence);
            await db.SaveChangesAsync();

            var service = new EmbeddingProfileActivationService(
                new EmbeddingProfileRepository(db),
                provisioner,
                new PostgresEmbeddingBuildStore(db),
                new RagEvaluationEvidenceRepository(db),
                new PostgresEmbeddingProfileActivationStore(db),
                new RagPromotionPolicy(RagPromotionRequirements.FoundationDefault),
                TimeProvider.System);

            var result = await service.ActivateAsync(
                candidateId,
                evidence.Id);

            Assert.Equal(BaselineProfileId, result.PreviousActiveProfileId);
            Assert.Equal(candidateId, result.ActiveProfileId);

            db.ChangeTracker.Clear();

            var activeProfiles = await db.EmbeddingProfiles
                .Where(x => x.Status == EmbeddingProfileStatus.Active)
                .Select(x => x.Id)
                .ToListAsync();

            Assert.Single(activeProfiles);
            Assert.Equal(candidateId, activeProfiles[0]);

            var baselineStatus = await db.EmbeddingProfiles
                .Where(x => x.Id == BaselineProfileId)
                .Select(x => x.Status)
                .SingleAsync();
            var candidateStatus = await db.EmbeddingProfiles
                .Where(x => x.Id == candidateId)
                .Select(x => x.Status)
                .SingleAsync();

            Assert.Equal(EmbeddingProfileStatus.Retired, baselineStatus);
            Assert.Equal(EmbeddingProfileStatus.Active, candidateStatus);
        }
        finally
        {
            await CleanupCandidateAsync(db, provisioner, candidateId, descriptor);
        }
    }

    [Fact]
    public async Task Postgres_Activation_Failure_Preserves_Current_Active_Profile()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ICEHOTT_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var db = CreateContext(connectionString);
        await db.Database.MigrateAsync();

        var baseline = await db.EmbeddingProfiles
            .SingleAsync(x => x.Id == BaselineProfileId);
        Assert.Equal(EmbeddingProfileStatus.Active, baseline.Status);

        var candidateId = Guid.NewGuid();
        var candidate = CreateBuildingProfile(candidateId);
        db.EmbeddingProfiles.Add(candidate);
        await db.SaveChangesAsync();

        var descriptor = ToDescriptor(candidate);
        var provisioner = new PostgresVectorIndexProvisioner(db);

        try
        {
            await provisioner.EnsureBuildIndexAsync(descriptor);

            var store = new PostgresEmbeddingProfileActivationStore(db);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ActivateAsync(
                    candidateId,
                    Guid.NewGuid(),
                    offlineEvidenceId: null,
                    DateTimeOffset.UtcNow));

            db.ChangeTracker.Clear();

            var baselineStatus = await db.EmbeddingProfiles
                .Where(x => x.Id == BaselineProfileId)
                .Select(x => x.Status)
                .SingleAsync();
            var candidateStatus = await db.EmbeddingProfiles
                .Where(x => x.Id == candidateId)
                .Select(x => x.Status)
                .SingleAsync();

            Assert.Equal(EmbeddingProfileStatus.Active, baselineStatus);
            Assert.Equal(EmbeddingProfileStatus.Building, candidateStatus);
            Assert.Equal(
                1,
                await db.EmbeddingProfiles.CountAsync(
                    x => x.Status == EmbeddingProfileStatus.Active));
        }
        finally
        {
            await CleanupCandidateAsync(db, provisioner, candidateId, descriptor);
        }
    }

    private static ICEHOTTDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ICEHOTTDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ICEHOTTDbContext(options);
    }

    private static EmbeddingProfile CreateBuildingProfile(Guid id) =>
        new(
            id,
            $"phase36b-it-{id:N}",
            "icehott-ai-runtime",
            "deterministic-64d",
            64,
            $"it-{id:N}",
            36,
            "cosine",
            "unit",
            EmbeddingProfileStatus.Building,
            DateTimeOffset.UtcNow);

    private static EmbeddingProfileDescriptor ToDescriptor(
        EmbeddingProfile profile) =>
        new(
            profile.Id,
            profile.Key,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.Version,
            profile.IndexVersion,
            profile.DistanceMetric,
            profile.Normalization);

    private static RagEvaluationEvidence CreatePassingEvidence(
        Guid profileId,
        EmbeddingProfileDescriptor profile) =>
        new(
            Guid.NewGuid(),
            "rag-v1",
            profileId,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.IndexVersion,
            1,
            1,
            1,
            1,
            0,
            RagEvaluationKind.Deterministic,
            "postgres-it-v1",
            DateTimeOffset.UtcNow);

    private static async Task CleanupCandidateAsync(
        ICEHOTTDbContext db,
        PostgresVectorIndexProvisioner provisioner,
        Guid candidateId,
        EmbeddingProfileDescriptor descriptor)
    {
        db.ChangeTracker.Clear();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE embedding_profiles SET \"Status\" = 'Retired' WHERE \"Id\" = {candidateId} AND \"Status\" = 'Active';");

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE embedding_profiles SET \"Status\" = 'Active' WHERE \"Id\" = {BaselineProfileId} AND \"Status\" <> 'Active';");

            await transaction.CommitAsync();
        }

        db.ChangeTracker.Clear();

        var candidate = await db.EmbeddingProfiles
            .SingleOrDefaultAsync(x => x.Id == candidateId);

        if (candidate is not null)
        {
            if (candidate.Status == EmbeddingProfileStatus.Building)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE embedding_profiles SET \"Status\" = 'Retired' WHERE \"Id\" = {candidateId};");
                db.ChangeTracker.Clear();
            }

            await provisioner.DropRetiredIndexAsync(descriptor);

            await db.RagEvaluationEvidence
                .Where(x => x.EmbeddingProfileId == candidateId)
                .ExecuteDeleteAsync();

            await db.EmbeddingProfiles
                .Where(x => x.Id == candidateId)
                .ExecuteDeleteAsync();
        }
    }
}
