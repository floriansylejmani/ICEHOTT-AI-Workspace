using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Agents;

namespace ICEHOTT.Application.Agents;

public sealed class AgentService(
    IWorkspaceRepository workspaces,
    IConversationRepository conversations,
    IVectorStore vectorStore,
    IAiRuntimeClient aiRuntime,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<AgentResult<IReadOnlyList<ConversationSummary>>> ListAsync(
        Guid userId,
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        var items = await conversations.ListAsync(workspaceId, cancellationToken);
        return new(items.Select(MapSummary).ToArray(), null);
    }

    public async Task<AgentResult<ConversationView>> GetAsync(
        Guid userId,
        Guid workspaceId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        var conversation = await conversations.FindAsync(workspaceId, conversationId, cancellationToken);
        if (conversation is null) return new(null, "conversation_not_found");

        var messages = await conversations.ListMessagesAsync(workspaceId, conversationId, cancellationToken);
        var citations = await conversations.ListCitationsAsync(workspaceId, conversationId, cancellationToken);
        return new(MapConversation(conversation, messages, citations), null);
    }

    public async Task<AgentResult<ChatReply>> SendAsync(
        Guid userId,
        Guid workspaceId,
        Guid? conversationId,
        string content,
        CancellationToken cancellationToken = default)
    {
        var normalizedContent = content.Trim();
        if (string.IsNullOrWhiteSpace(normalizedContent))
            return new(null, "message_required");
        if (normalizedContent.Length > 12000)
            return new(null, "message_too_long");

        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        var now = clock.GetUtcNow();
        Conversation conversation;
        if (conversationId is null)
        {
            conversation = new Conversation(
                Guid.NewGuid(),
                workspaceId,
                userId,
                BuildTitle(normalizedContent),
                now);
            await conversations.AddConversationAsync(conversation, cancellationToken);
        }
        else
        {
            var existing = await conversations.FindAsync(workspaceId, conversationId.Value, cancellationToken);
            if (existing is null) return new(null, "conversation_not_found");
            conversation = existing;
        }

        var userMessage = new ConversationMessage(
            Guid.NewGuid(),
            conversation.Id,
            workspaceId,
            MessageRole.User,
            normalizedContent,
            now);

        await conversations.AddMessageAsync(userMessage, cancellationToken);
        conversation.Touch(now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var embeddingReply = await aiRuntime.EmbedAsync([normalizedContent], cancellationToken);
        if (embeddingReply.Embeddings.Count != 1)
            throw new AiRuntimeUnavailableException("AI runtime returned an invalid query embedding.");

        var matches = (await vectorStore.SearchAsync(
                workspaceId,
                normalizedContent,
                embeddingReply.Embeddings[0],
                5,
                cancellationToken))
            .Where(match => match.Score >= 0.15)
            .ToArray();

        var history = await conversations.ListMessagesAsync(workspaceId, conversation.Id, cancellationToken);
        var runtimeReply = await aiRuntime.ReplyAsync(
            new AiRuntimeRequest(
                workspaceId,
                userId,
                conversation.Id,
                history.Select(MapRuntimeTurn).ToArray(),
                matches.Select(match => new AiRuntimeKnowledge(
                    match.ChunkId,
                    match.DocumentId,
                    match.Title,
                    match.SourceName,
                    match.Content,
                    match.Score)).ToArray()),
            cancellationToken);

        var assistantMessage = new ConversationMessage(
            Guid.NewGuid(),
            conversation.Id,
            workspaceId,
            MessageRole.Assistant,
            runtimeReply.Content,
            clock.GetUtcNow());

        var citations = matches.Select(match => new ConversationMessageCitation(
            Guid.NewGuid(),
            assistantMessage.Id,
            workspaceId,
            match.DocumentId,
            match.ChunkId,
            match.Title,
            match.SourceName,
            match.Score,
            assistantMessage.CreatedAtUtc)).ToArray();

        await conversations.AddMessageAsync(assistantMessage, cancellationToken);
        await conversations.AddCitationsAsync(citations, cancellationToken);
        conversation.Touch(assistantMessage.CreatedAtUtc);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new(new ChatReply(
            conversation.Id,
            MapMessage(userMessage, []),
            MapMessage(assistantMessage, citations),
            runtimeReply.Provider,
            runtimeReply.Model), null);
    }

    private static ConversationSummary MapSummary(Conversation conversation) =>
        new(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAtUtc,
            conversation.UpdatedAtUtc);

    private static ConversationView MapConversation(
        Conversation conversation,
        IReadOnlyList<ConversationMessage> messages,
        IReadOnlyList<ConversationMessageCitation> citations)
    {
        var citationsByMessage = citations
            .GroupBy(x => x.MessageId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ConversationMessageCitation>)group.ToArray());

        return new(
            conversation.Id,
            conversation.WorkspaceId,
            conversation.Title,
            conversation.CreatedAtUtc,
            conversation.UpdatedAtUtc,
            messages.Select(message => MapMessage(
                message,
                citationsByMessage.TryGetValue(message.Id, out var messageCitations)
                    ? messageCitations
                    : [])).ToArray());
    }

    private static MessageView MapMessage(
        ConversationMessage message,
        IReadOnlyList<ConversationMessageCitation> citations) =>
        new(
            message.Id,
            message.Role,
            message.Content,
            message.CreatedAtUtc,
            citations.Select(citation => new CitationView(
                citation.DocumentId,
                citation.ChunkId,
                citation.Title,
                citation.SourceName,
                citation.Score)).ToArray());

    private static AiRuntimeTurn MapRuntimeTurn(ConversationMessage message) =>
        new(message.Role switch
        {
            MessageRole.User => "user",
            MessageRole.Assistant => "assistant",
            MessageRole.System => "system",
            _ => "user"
        }, message.Content);

    private static string BuildTitle(string content)
    {
        const int maxLength = 80;
        var singleLine = string.Join(" ", content
            .Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .Trim();

        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength].TrimEnd() + "…";
    }
}
