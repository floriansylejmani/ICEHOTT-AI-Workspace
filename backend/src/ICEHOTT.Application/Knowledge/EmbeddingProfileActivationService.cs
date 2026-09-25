using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed class EmbeddingProfileActivationService(
    IEmbeddingProfileRepository profiles,
    IRagEvaluationEvidenceRepository evidenceRepository,
    IVectorIndexProvisioner indexes,
    IEmbeddingProfileCoverageService coverage,
    IEmbeddingProfileActivationStore activationStore,
    RagEvaluationPromotionPolicy promotionPolicy,
    TimeProvider clock)
{
    public async Task ActivateAsync(
        Guid buildingProfileId,
        Guid evidenceId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await profiles.FindAsync(
            buildingProfileId,
            cancellationToken)
            ?? throw new InvalidOperationException("Candidate embedding profile was not found.");

        if (candidate.Status != EmbeddingProfileStatus.Building)
            throw new InvalidOperationException(
                "Only a Building embedding profile can be activated.");
        var active = await profiles.GetActiveAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "An Active embedding profile is required before promotion.");

        if (active.Id == candidate.Id)
            throw new InvalidOperationException(
                "Candidate profile cannot already be the serving profile.");

        var descriptor = ToDescriptor(candidate);
        descriptor.Validate();

        var evidence = await evidenceRepository.FindAsync(
            evidenceId,
            cancellationToken)
            ?? throw new InvalidOperationException("Evaluation evidence was not found.");

        promotionPolicy.Validate(evidence, descriptor);

        if (!await indexes.IsIndexReadyAsync(descriptor, cancellationToken))
            throw new InvalidOperationException(
                "Candidate embedding profile HNSW index is not ready.");
        var currentCoverage = await coverage.GetCoverageAsync(
            descriptor,
            cancellationToken);

        if (!currentCoverage.IsComplete)
            throw new InvalidOperationException(
                $"Candidate embedding coverage is incomplete: " +
                $"{currentCoverage.EmbeddedReadyChunks}/{currentCoverage.ExpectedReadyChunks}.");

        await activationStore.ActivateAsync(
            candidate.Id,
            evidence.Id,
            clock.GetUtcNow(),
            cancellationToken);
    }

    private static EmbeddingProfileDescriptor ToDescriptor(EmbeddingProfile profile) =>
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
}
