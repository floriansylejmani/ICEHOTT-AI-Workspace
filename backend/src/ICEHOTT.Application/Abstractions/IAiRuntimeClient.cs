namespace ICEHOTT.Application.Abstractions;

public sealed record AiRuntimeTurn(string Role, string Content);

public sealed record AiRuntimeRequest(
    Guid WorkspaceId,
    Guid UserId,
    Guid ConversationId,
    IReadOnlyList<AiRuntimeTurn> Messages);

public sealed record AiRuntimeReply(string Content, string Provider, string Model);

public interface IAiRuntimeClient
{
    Task<AiRuntimeReply> ReplyAsync(AiRuntimeRequest request, CancellationToken cancellationToken = default);
}

public sealed class AiRuntimeUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
