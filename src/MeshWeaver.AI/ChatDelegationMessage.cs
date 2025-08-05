using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

/// <summary>
/// Represents a delegation message in the chat system
/// </summary>
public class ChatDelegationMessage : ChatMessage
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

    public ChatDelegationMessage(
        string delegatingAgent, 
        string targetAgent, 
        string delegationMessage, 
        bool requiresUserFeedback = false) 
        : base(ChatRole.Assistant, CreateDisplayContent(delegatingAgent, targetAgent, delegationMessage, requiresUserFeedback))
    {
        DelegatingAgent = delegatingAgent;
        TargetAgent = targetAgent;
        DelegationMessage = delegationMessage;
        RequiresUserFeedback = requiresUserFeedback;
        AuthorName = new(delegatingAgent);
    }

    private static IList<AIContent> CreateDisplayContent(
        string delegatingAgent, 
        string targetAgent, 
        string delegationMessage, 
        bool requiresUserFeedback)
    {
        var displayText = requiresUserFeedback
            ? $"Requesting user feedback before delegating to @{targetAgent}: {delegationMessage}"
            : $"Delegating to @{targetAgent}: {delegationMessage}";
            
        return [new TextContent(displayText)];
    }
}