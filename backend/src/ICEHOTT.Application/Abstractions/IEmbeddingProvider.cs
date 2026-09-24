namespace ICEHOTT.Application.Abstractions;

public sealed record EmbeddingBatch(
    int Dimensions,
    IReadOnlyList<IReadOnlyList<float>> Embeddings,
    string Provider,
    string Model);

public interface IEmbeddingProvider
{
    Task<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
