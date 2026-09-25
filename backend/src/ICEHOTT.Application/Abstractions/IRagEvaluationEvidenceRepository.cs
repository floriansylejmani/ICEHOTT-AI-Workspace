using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Abstractions;

public interface IRagEvaluationEvidenceRepository
{
    Task AddAsync(
        RagEvaluationEvidence evidence,
        CancellationToken cancellationToken = default);

    Task<RagEvaluationEvidence?> FindAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
