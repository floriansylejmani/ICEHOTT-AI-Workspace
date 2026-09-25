using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed class EmbeddingProfileActivationService(
    IEmbeddingProfileRepository profiles,
    IVectorIndexProvisioner indexProvisioner,
    IEmbeddingBuildStore buildStore,
    IRagEvaluationEvidenceRepository evidenceRepository,
    IEmbeddingProfileActivationStore activationStore,
    RagPromotionPolicy promotionPolicy,
    TimeProvider clock)
{
    public async Task<EmbeddingProfileActivationResult> ActivateAsync(
        Guid candidateProfileId,
        Guid deterministicEvidenceId,
        Guid? offlineEvidenceId = null,
        CancellationToken cancellationToken = default)
    {
        var candidate = await profiles.FindAsync(
            candidateProfileId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Candidate embedding profile was not found.");

        if (candidate.Status != EmbeddingProfileStatus.Building)
            throw new InvalidOperationException(
                $"Candidate profile must be Building, but is {candidate.Status}.");

        var active = await profiles.GetActiveAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "No Active embedding profile exists.");

        if (active.Id == candidate.Id)
            throw new InvalidOperationException(
                "Candidate profile cannot already be the Active profile.");

        var descriptor =
            ServingEmbeddingProfileResolver.ToDescriptor(candidate);
        descriptor.Validate();

        if (!await indexProvisioner.IsIndexReadyAsync(
                descriptor,
                cancellationToken))
            throw new InvalidOperationException(
                "Candidate profile HNSW index is not ready.");

        var coverage = await buildStore.GetCoverageAsync(
            candidate.Id,
            cancellationToken);

        if (!coverage.IsComplete)
            throw new InvalidOperationException(
                $"Candidate profile coverage is incomplete: {coverage.EmbeddedChunkCount}/{coverage.ReadyChunkCount} ready chunks.");

        var deterministicEvidence = await evidenceRepository.FindAsync(
            deterministicEvidenceId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Deterministic evaluation evidence was not found.");

        RagEvaluationEvidence? offlineEvidence = null;
        if (offlineEvidenceId is not null)
        {
            offlineEvidence = await evidenceRepository.FindAsync(
                offlineEvidenceId.Value,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "Offline evaluation evidence was not found.");
        }

        promotionPolicy.Validate(
            descriptor,
            deterministicEvidence,
            offlineEvidence);

        return await activationStore.ActivateAsync(
            candidate.Id,
            deterministicEvidence.Id,
            offlineEvidence?.Id,
            clock.GetUtcNow(),
            cancellationToken);
    }
}
