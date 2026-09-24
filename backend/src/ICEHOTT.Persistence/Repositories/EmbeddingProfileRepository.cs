using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class EmbeddingProfileRepository(ICEHOTTDbContext db) : IEmbeddingProfileRepository
{
    public Task<EmbeddingProfile?> GetActiveAsync(CancellationToken cancellationToken = default) =>
        db.EmbeddingProfiles.SingleOrDefaultAsync(
            p => p.Status == EmbeddingProfileStatus.Active,
            cancellationToken);

    public Task<EmbeddingProfile?> GetBuildingAsync(CancellationToken cancellationToken = default) =>
        db.EmbeddingProfiles.SingleOrDefaultAsync(
            p => p.Status == EmbeddingProfileStatus.Building,
            cancellationToken);

    public Task<EmbeddingProfile?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.EmbeddingProfiles.SingleOrDefaultAsync(
            p => p.Id == id,
            cancellationToken);

    public async Task AddAsync(EmbeddingProfile profile, CancellationToken cancellationToken = default) =>
        await db.EmbeddingProfiles.AddAsync(profile, cancellationToken);
}
