namespace ICEHOTT.API.Models;

public sealed record SendAgentMessageRequest(Guid? ConversationId, string Content);
