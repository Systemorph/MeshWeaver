using MeshWeaver.Layout;

namespace MeshWeaver.AI;

public record AgentChatControl() : UiControl<AgentChatControl>("MeshWeaver.AI", "1.0.0")
{
    /// <summary>
    /// The agent context. Can be either an AgentContext instance or a JsonPointerReference for reactive binding.
    /// </summary>
    public object? Context { get; init; }

    /// <summary>
    /// The title to display in the chat header. Defaults to "AI Chat".
    /// </summary>
    public object? Title { get; init; }

    /// <summary>
    /// Path to the Chat MeshNode for hierarchical storage.
    /// When set, the chat will be stored as a MeshNode instead of using thread-based storage.
    /// </summary>
    public string? ChatNodePath { get; init; }

    public AgentChatControl WithContext(AgentContext context) => this with { Context = context };
    public AgentChatControl WithContextReference(object contextReference) => this with { Context = contextReference };
    public AgentChatControl WithTitle(string title) => this with { Title = title };
    public AgentChatControl WithChatNodePath(string path) => this with { ChatNodePath = path };
}
