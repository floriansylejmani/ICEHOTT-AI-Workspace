using ICEHOTT.Domain.Agents;

namespace ICEHOTT.Application.Abstractions;

public interface IConversationRepository
{
    Task AddConversationAsync(Conversation conversation, CancellationToken cancellationToken = default);
    Task AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default);
    Task AddCitationsAsync(
        IReadOnlyList<ConversationMessageCitation> citations,
        CancellationToken cancellationToken = default);
    Task<Conversation?> FindAsync(Guid workspaceId, Guid conversationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Conversation>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(
        Guid workspaceId,
        Guid conversationId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationMessageCitation>> ListCitationsAsync(
        Guid workspaceId,
        Guid conversationId,
        CancellationToken cancellationToken = default);
}
