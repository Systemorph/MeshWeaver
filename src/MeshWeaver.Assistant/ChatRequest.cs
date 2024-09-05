namespace MeshWeaver.Assistant;

public record ChatRequest(IEnumerable<AssistantChatMessage> Messages);

public record AssistantChatMessage
{
    public bool IsAssistant { get; set; }
    public required string Text { get; set; }
}
