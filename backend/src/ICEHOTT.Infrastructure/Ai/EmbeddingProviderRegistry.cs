using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Infrastructure.Ai;

/// <summary>
/// Registry of embedding providers keyed by provider name (case-insensitive).
/// Built from all registered <see cref="IEmbeddingProvider"/> instances at DI composition time.
/// Throws <see cref="EmbeddingProviderException"/> with Configuration kind on unknown provider names.
/// </summary>
public sealed class EmbeddingProviderRegistry : IEmbeddingProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IEmbeddingProvider> _providers;

    public EmbeddingProviderRegistry(IEnumerable<IEmbeddingProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Provider, StringComparer.OrdinalIgnoreCase);
    }

    public IEmbeddingProvider Resolve(string provider)
    {
        if (_providers.TryGetValue(provider, out var impl))
            return impl;

        var registered = _providers.Count > 0
            ? string.Join(", ", _providers.Keys)
            : "(none)";

        throw new EmbeddingProviderException(
            $"No embedding provider is registered for '{provider}'. " +
            $"Registered providers: {registered}. " +
            "Add the provider adapter and register it before use. This error is non-retryable.",
            EmbeddingFailureKind.Configuration);
    }
}
