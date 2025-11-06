using System.ComponentModel;
using MeshWeaver.Data;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Plugin that provides context management functionality for agents.
/// </summary>
public class ChatPlugin(IAgentChat agentChat)
{
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
}
