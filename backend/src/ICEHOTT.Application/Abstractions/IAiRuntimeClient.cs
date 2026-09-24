namespace ICEHOTT.Application.Abstractions;

public sealed record AiRuntimeTurn(string Role, string Content);

public sealed record AiRuntimeKnowledge(
    Guid ChunkId,
    Guid DocumentId,
    string Title,
    string? SourceName,
    string Content,
    double Score);

public sealed record AiRuntimeRequest(
    Guid WorkspaceId,
    Guid UserId,
    Guid ConversationId,
    IReadOnlyList<AiRuntimeTurn> Messages,
    IReadOnlyList<AiRuntimeKnowledge> Knowledge);

public sealed record AiRuntimeReply(string Content, string Provider, string Model);

public sealed record AiEmbeddingReply(
    int Dimensions,
    IReadOnlyList<IReadOnlyList<float>> Embeddings);

public interface IAiRuntimeClient
{
    Task<AiRuntimeReply> ReplyAsync(AiRuntimeRequest request, CancellationToken cancellationToken = default);

    Task<AiEmbeddingReply> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}

public sealed class AiRuntimeUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
