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