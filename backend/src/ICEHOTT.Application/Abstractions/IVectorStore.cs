namespace ICEHOTT.Application.Abstractions;

public sealed record VectorEmbedding(Guid ChunkId, IReadOnlyList<float> Values);

public sealed record KnowledgeMatch(
    Guid ChunkId,
    Guid DocumentId,
    string Title,
    string? SourceName,
    string Content,
    double Score);

public interface IVectorStore
{
    Task<bool> IsProfileReadyAsync(
        EmbeddingProfileDescriptor profile,
        CancellationToken cancellationToken = default);

    Task StoreManyAsync(
        Guid workspaceId,
        EmbeddingProfileDescriptor profile,
        IReadOnlyList<VectorEmbedding> embeddings,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(
        Guid workspaceId,
        EmbeddingProfileDescriptor profile,
        string queryText,
        IReadOnlyList<float> queryEmbedding,
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed class VectorStoreUnavailableException(
    string message,
    bool retryable = true,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public bool IsRetryable { get; } = retryable;
}
