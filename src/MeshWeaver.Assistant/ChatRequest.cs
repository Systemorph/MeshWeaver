namespace MeshWeaver.Assistant;

public record ChatRequest(IEnumerable<AssistantChatMessage> Messages);

public record AssistantChatMessage
{
    public bool IsAssistant { get; set; }
    public required string Text { get; set; }

    public List<ReferenceItem> References { get; init; } = new();

    public AssistantChatMessage SetText(Func<string, string> update)
        => this with {Text = update(Text)};
}

public record ReferenceItem(string Title, string Url);
