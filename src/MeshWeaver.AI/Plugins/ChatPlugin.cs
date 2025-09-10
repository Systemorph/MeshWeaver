using System.ComponentModel;
using MeshWeaver.Data;
using Microsoft.SemanticKernel;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Plugin that provides delegation functionality for agents to delegate tasks to other agents.
/// </summary>
public class ChatPlugin(IAgentChat agentChat)
{
    /// <summary>
    /// Delegates a task to another agent. This is useful when you need to hand off a task to a specialized agent that can handle it better.
    /// If you feel a message does not fall within your responsibility or expertise, delegate to the default agent.
    /// </summary>
    /// <param name="agentName"></param>
    /// <param name="message"></param>
    /// <param name="askUserFeedback"></param>
    /// <returns></returns>
    [KernelFunction]
    [Description("Delegate a task to another agent. Use this when you need to hand off a task to a specialized agent. If the message does not fall within your responsibility, delegate to the default agent.")]
    public string Delegate(
        [Description("The exact name of the agent to delegate to (use 'default' for the default agent)")] string agentName,
        [Description("The message or task to send to the agent")] string message,
        [Description("Whether to ask for user feedback before proceeding (default: false)")] bool askUserFeedback = false)
    {
        return agentChat.Delegate(agentName, message, askUserFeedback);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="address"></param>
    /// <param name="areaName"></param>
    /// <param name="areaId"></param>
    /// <returns></returns>

    [KernelFunction]
    [Description("Sets the chat context to a particular address, layout area or layout area id.")]
    public string SetContext([Description("Address to be set")] string? address = null,
        [Description("Name of the layout are to be set")] string? areaName = null,
        [Description("Id of the layout area to be set.")] string? areaId = null)
    {
        var currentContext = agentChat.Context ?? new();
        if (address is not null && address != "null")
            currentContext = currentContext with { Address = address };
        if (areaName is not null && areaName != "null")
            currentContext = currentContext with { LayoutArea = new LayoutAreaReference(areaName) };
        if (areaId is not null && areaId != "null")
            currentContext = currentContext with { LayoutArea = (currentContext.LayoutArea ?? new(areaName ?? "null"))with{Id = areaId} };

        agentChat.SetContext(currentContext);
        return "Context set successfully.";
    }
}
