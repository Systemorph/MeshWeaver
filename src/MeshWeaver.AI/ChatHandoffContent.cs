using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

/// <summary>
/// Represents a handoff content in chat messages.
/// Unlike delegation (isolated context, result returned to caller),
/// a handoff transfers control entirely to the target agent on the shared thread.
/// </summary>
public class ChatHandoffContent : AIContent
{
    /// <summary>
    /// The agent that initiated the handoff.
    /// </summary>
    public string SourceAgent { get; }

    /// <summary>
    /// The agent receiving control.
    /// </summary>
    public string TargetAgent { get; }

    /// <summary>
    /// The message/context passed to the target agent.
    /// </summary>
    public string HandoffMessage { get; }

    public ChatHandoffContent(string sourceAgent, string targetAgent, string handoffMessage)
    {
        SourceAgent = sourceAgent;
        TargetAgent = targetAgent;
        HandoffMessage = handoffMessage;
    }
}
