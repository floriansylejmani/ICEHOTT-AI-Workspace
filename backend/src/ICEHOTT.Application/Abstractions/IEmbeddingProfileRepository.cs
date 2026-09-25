using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Abstractions;

/// <summary>
/// Persistence boundary for embedding profiles.
/// All status filtering (Active, Building, etc.) must go through typed methods,
/// never through ad-hoc status string comparisons in callers.
/// </summary>
public interface IEmbeddingProfileRepository
{
    /// <summary>Returns the deployment-wide Active profile, or null if none exists.</summary>
    Task<EmbeddingProfile?> GetActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the deployment-wide Building profile, or null if none exists.
    /// Phase 3.6B-Foundation allows at most one Building profile.
    /// </summary>
    Task<EmbeddingProfile?> GetBuildingAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a profile by primary key, or null if not found.</summary>
    Task<EmbeddingProfile?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Stages a new profile for insertion. Caller must call IUnitOfWork.SaveChangesAsync.</summary>
    Task AddAsync(EmbeddingProfile profile, CancellationToken cancellationToken = default);
}
