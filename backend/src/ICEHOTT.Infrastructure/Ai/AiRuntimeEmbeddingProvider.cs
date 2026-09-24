using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Infrastructure.Ai;

public sealed class AiRuntimeEmbeddingProvider(IAiRuntimeClient runtime) : IEmbeddingProvider
{
    public async Task<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var reply = await runtime.EmbedAsync(texts, cancellationToken);
        return new EmbeddingBatch(
            reply.Dimensions,
            reply.Embeddings,
            "icehott-ai-runtime",
            $"deterministic-{reply.Dimensions}d");
    }
}
