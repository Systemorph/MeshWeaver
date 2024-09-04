namespace MeshWeaver.Assistant;

public record AssistantChatRequest(IReadOnlyList<AssistantChatRequestMessage> Messages);

public record AssistantChatRequestMessage
{
    public bool IsAssistant { get; set; }
    public required string Text { get; set; }
}
