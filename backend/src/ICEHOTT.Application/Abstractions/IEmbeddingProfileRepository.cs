using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Abstractions;

public interface IEmbeddingProfileRepository
{
    Task<EmbeddingProfile?> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<EmbeddingProfile?> GetBuildingAsync(CancellationToken cancellationToken = default);
    Task<EmbeddingProfile?> FindAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(EmbeddingProfile profile, CancellationToken cancellationToken = default);
}
