using ICEHOTT.Domain.Agents;

namespace ICEHOTT.Application.Agents;

public sealed record ConversationSummary(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record MessageView(
    Guid Id,
    MessageRole Role,
    string Content,
    DateTimeOffset CreatedAtUtc);

public sealed record ConversationView(
    Guid Id,
    Guid WorkspaceId,
    string Title,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<MessageView> Messages);

public sealed record ChatReply(
    Guid ConversationId,
    MessageView UserMessage,
    MessageView AssistantMessage,
    string Provider,
    string Model);

public sealed record AgentResult<T>(T? Value, string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null;
}
