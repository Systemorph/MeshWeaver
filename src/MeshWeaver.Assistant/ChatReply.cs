using Azure.AI.OpenAI.Chat;

namespace MeshWeaver.Assistant;

public record ChatReply
{
    public string Text { get; init; }
    public IReadOnlyList<AzureChatCitation> Citations { get; init; }
}
