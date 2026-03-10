namespace Esportra.Contracts.Requests;

public sealed record CreateConversationRequest(
    string   Type,
    string?  Title          = null,
    string[]? ParticipantIds = null);

public sealed record SendMessageRequest(
    string  Content,
    string? MessageType = "text",
    string? Attachments = null);

public sealed record EditMessageRequest(string Content);
