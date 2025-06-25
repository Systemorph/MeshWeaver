using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Plugin that provides delegation functionality for agents to delegate tasks to other agents.
/// </summary>
public class DelegationPlugin(IAgentChat agentChat)
{
    [KernelFunction]
    [Description("Delegate a task to another agent. Use this when you need to hand off a task to a specialized agent.")]
    public string Delegate(
        [Description("The exact name of the agent to delegate to")] string agentName,
        [Description("The message or task to send to the agent")] string message,
        [Description("Whether to ask for user feedback before proceeding (default: false)")] bool askUserFeedback = false)
    {
        return agentChat.Delegate(agentName, message, askUserFeedback);
    }
}
