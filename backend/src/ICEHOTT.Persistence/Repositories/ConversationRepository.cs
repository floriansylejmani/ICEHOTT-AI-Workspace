using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Agents;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class ConversationRepository(ICEHOTTDbContext db) : IConversationRepository
{
    public async Task AddConversationAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default) =>
        await db.Conversations.AddAsync(conversation, cancellationToken);

    public async Task AddMessageAsync(
        ConversationMessage message,
        CancellationToken cancellationToken = default) =>
        await db.ConversationMessages.AddAsync(message, cancellationToken);

    public async Task AddCitationsAsync(
        IReadOnlyList<ConversationMessageCitation> citations,
        CancellationToken cancellationToken = default)
    {
        if (citations.Count > 0)
            await db.ConversationMessageCitations.AddRangeAsync(citations, cancellationToken);
    }

    public Task<Conversation?> FindAsync(
        Guid workspaceId,
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        db.Conversations.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.Id == conversationId,
            cancellationToken);

    public async Task<IReadOnlyList<Conversation>> ListAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        var items = await db.Conversations.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        return items.OrderByDescending(x => x.UpdatedAtUtc).Take(50).ToArray();
    }

    public async Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(
        Guid workspaceId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var items = await db.ConversationMessages.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.ConversationId == conversationId)
            .ToListAsync(cancellationToken);

        return items.OrderBy(x => x.CreatedAtUtc).ToArray();
    }

    public async Task<IReadOnlyList<ConversationMessageCitation>> ListCitationsAsync(
        Guid workspaceId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var messageIds = await db.ConversationMessages.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.ConversationId == conversationId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (messageIds.Count == 0) return [];

        return await db.ConversationMessageCitations.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && messageIds.Contains(x.MessageId))
            .OrderByDescending(x => x.Score)
            .ToListAsync(cancellationToken);
    }
}
