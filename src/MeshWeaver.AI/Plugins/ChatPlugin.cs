using System.ComponentModel;
using MeshWeaver.Data;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Plugin that provides delegation and context management functionality for agents.
/// </summary>
public class ChatPlugin
{
    private readonly IAgentChat agentChat;
    private readonly ILogger<ChatPlugin>? logger;

    public ChatPlugin(IAgentChat agentChat, ILogger<ChatPlugin>? logger = null)
    {
        this.agentChat = agentChat;
        this.logger = logger;
    }

    /// <summary>
    /// Sets the chat context to a particular address, layout area or layout area id.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="areaName"></param>
    /// <param name="areaId"></param>
    /// <returns></returns>
    [Description("Sets the chat context to a particular address, layout area or layout area id.")]
    public string SetContext([Description("Address to be set")] string? address = null,
        [Description("Name of the layout are to be set")] string? areaName = null,
        [Description("Id of the layout area to be set.")] string? areaId = null)
    {
        logger?.LogInformation("SetContext called with address={Address}, areaName={AreaName}, areaId={AreaId}",
            address, areaName, areaId);

        var currentContext = agentChat.Context ?? new();
        if (address is not null && address != "null")
            currentContext = currentContext with { Address = address };
        if (areaName is not null && areaName != "null")
            currentContext = currentContext with { LayoutArea = new LayoutAreaReference(areaName) };
        if (areaId is not null && areaId != "null")
            currentContext = currentContext with { LayoutArea = (currentContext.LayoutArea ?? new(areaName ?? "null")) with { Id = areaId } };

        agentChat.SetContext(currentContext);
        return "Context set successfully.";
    }

    public IList<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(SetContext)
        ];
    }

    /// <summary>
    /// Creates a delegation tool for a specific target agent.
    /// Instead of using AsAIFunction which doesn't stream properly, this creates a custom
    /// delegation function that signals AgentChatClient to invoke sub-agents in streaming mode.
    /// </summary>
    /// <param name="targetAgentName">The name of the agent to delegate to</param>
    /// <param name="targetAgentDescription">Description of when to use this agent</param>
    /// <param name="logger">Optional logger for delegation events</param>
    /// <returns>An AITool that can be used to delegate to the target agent</returns>
    public static AITool CreateDelegationTool(
        string targetAgentName,
        string targetAgentDescription,
        ILogger? logger = null)
    {
        string DelegateToAgent([Description("The message/instructions to send to the specialized agent. " +
            "CRITICAL: After calling this function, DO NOT output any additional text. " +
            "The specialized agent will handle the request and provide all necessary output to the user. " +
            "Your job is complete once you call this delegation function.")] string message)
        {
            logger?.LogInformation("Delegation requested to {TargetAgent} with message: {Message}",
                targetAgentName, message);

            // Return a special marker that AgentChatClient will detect
            // Format: __DELEGATE__|{targetAgentName}|{message}
            return $"__DELEGATE__|{targetAgentName}|{message}";
        }

        // Create the tool with a custom name and description
        // Add instruction to not continue after delegation
        var enhancedDescription = targetAgentDescription +
            " IMPORTANT: After delegating to this agent, you should not provide any additional output. " +
            "The specialist agent will handle everything and communicate directly with the user.";

        return AIFunctionFactory.Create(
            DelegateToAgent,
            name: targetAgentName,
            description: enhancedDescription);
    }
}
