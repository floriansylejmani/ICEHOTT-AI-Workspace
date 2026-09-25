using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class RagEvaluationEvidenceRepository(ICEHOTTDbContext db)
    : IRagEvaluationEvidenceRepository
{
    public async Task AddAsync(
        RagEvaluationEvidence evidence,
        CancellationToken cancellationToken = default) =>
        await db.RagEvaluationEvidence.AddAsync(evidence, cancellationToken);

    public Task<RagEvaluationEvidence?> FindAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        db.RagEvaluationEvidence
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
}
