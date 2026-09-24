using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Infrastructure.Ai;

public sealed class EmbeddingProviderRegistry : IEmbeddingProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IEmbeddingProvider> _providers;

    public EmbeddingProviderRegistry(IEnumerable<IEmbeddingProvider> providers)
    {
        var map = new Dictionary<string, IEmbeddingProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Provider))
                throw new InvalidOperationException("Embedding provider identity is required.");

            if (!map.TryAdd(provider.Provider, provider))
                throw new InvalidOperationException(
                    $"Duplicate embedding provider registration: {provider.Provider}.");
        }

        _providers = map;
    }

    public IEmbeddingProvider Resolve(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new EmbeddingProviderException(
                "Embedding provider is required.",
                EmbeddingFailureKind.Configuration);

        if (_providers.TryGetValue(provider, out var implementation))
            return implementation;

        throw new EmbeddingProviderException(
            $"Embedding provider '{provider}' is not registered.",
            EmbeddingFailureKind.Configuration);
    }
}
