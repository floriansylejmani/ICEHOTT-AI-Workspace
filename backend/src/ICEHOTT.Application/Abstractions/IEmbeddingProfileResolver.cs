namespace ICEHOTT.Application.Abstractions;

/// <summary>
/// Resolves the Active (serving) embedding profile for retrieval/query paths.
/// Retrieval must ONLY call this resolver — never the build resolver.
/// </summary>
public interface IServingEmbeddingProfileResolver
{
    /// <summary>
    /// Returns the Active profile descriptor.
    /// Throws <see cref="InvalidOperationException"/> if no Active profile exists.
    /// </summary>
    Task<EmbeddingProfileDescriptor> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the Building embedding profile for index-build/reindex paths.
/// Build orchestration must ONLY call this resolver — never the serving resolver.
/// Returns null when no Building profile exists (Phase 3.6A transitional state).
/// </summary>
public interface IBuildEmbeddingProfileResolver
{
    /// <summary>
    /// Returns the Building profile descriptor, or null if no Building profile exists.
    /// Never returns an Active or Retired profile.
    /// </summary>
    Task<EmbeddingProfileDescriptor?> ResolveAsync(CancellationToken cancellationToken = default);
}
