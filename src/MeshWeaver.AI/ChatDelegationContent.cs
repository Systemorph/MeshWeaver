using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

/// <summary>
/// Represents a delegation content in chat messages
/// </summary>
public class ChatDelegationContent : AIContent
{
    /// <summary>
    /// The agent that is delegating
    /// </summary>
    public string DelegatingAgent { get; }
    
    /// <summary>
    /// The agent being delegated to
    /// </summary>
    public string TargetAgent { get; }
    
    /// <summary>
    /// The message being sent to the target agent
    /// </summary>
    public string DelegationMessage { get; }
    
    /// <summary>
    /// Whether this is a delegation that requires user feedback first
    /// </summary>
    public bool RequiresUserFeedback { get; }

    /// <summary>
    /// Creates a delegation content entry describing one agent routing a task to another.
    /// </summary>
    /// <param name="delegatingAgent">The agent initiating the delegation.</param>
    /// <param name="targetAgent">The agent the task is being delegated to.</param>
    /// <param name="delegationMessage">The task/message sent to the target agent.</param>
    /// <param name="requiresUserFeedback">Whether the delegation requires user feedback before proceeding.</param>
    public ChatDelegationContent(
        string delegatingAgent,
        string targetAgent,
        string delegationMessage,
        bool requiresUserFeedback = false)
    {
        DelegatingAgent = delegatingAgent;
        TargetAgent = targetAgent;
        DelegationMessage = delegationMessage;
        RequiresUserFeedback = requiresUserFeedback;
    }

    /// <summary>
    /// Short summary of <see cref="DelegationMessage"/> for chip / header display:
    /// first non-empty line, truncated to ~40 chars. Empty when the message is
    /// missing — callers fall back to the bare "Delegating to {Agent}" shape.
    /// </summary>
    public string TaskSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DelegationMessage))
                return string.Empty;
            var firstLine = DelegationMessage.Split('\n', 2)[0].Trim();
            const int maxLen = 40;
            if (firstLine.Length > maxLen)
                firstLine = firstLine[..(maxLen - 1)] + "…";
            return firstLine;
        }
    }
}