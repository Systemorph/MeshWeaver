using MeshWeaver.Layout;

namespace MeshWeaver.AI;

public record AgentChatControl() : UiControl<AgentChatControl>("MeshWeaver.AI", "1.0.0")
{
    /// <summary>
    /// The agent context. Can be either an AgentContext instance or a JsonPointerReference for reactive binding.
    /// </summary>
    public object? Context { get; init; }

    public AgentChatControl WithContext(AgentContext context) => this with { Context = context };
    public AgentChatControl WithContextReference(object contextReference) => this with { Context = contextReference };
}
