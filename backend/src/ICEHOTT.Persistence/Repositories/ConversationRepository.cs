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
}
