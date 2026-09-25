using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Abstractions;

public interface IRagEvaluationEvidenceRepository
{
    Task<RagEvaluationEvidence?> FindAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        RagEvaluationEvidence evidence,
        CancellationToken cancellationToken = default);
}

public interface IEmbeddingProfileActivationStore
{
    Task ActivateAsync(
        Guid buildingProfileId,
        Guid evidenceId,
        DateTimeOffset activatedAtUtc,
        CancellationToken cancellationToken = default);
}
