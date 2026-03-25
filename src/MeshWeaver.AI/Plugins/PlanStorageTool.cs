using System.ComponentModel;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Creates a tool that stores execution plans as Markdown nodes under the current thread.
/// </summary>
public static class PlanStorageTool
{
    /// <summary>
    /// Creates the store_plan AITool.
    /// </summary>
    public static AITool Create(IMessageHub hub, IAgentChat chat)
    {
        async Task<string> StorePlan(
            [Description("The plan content in Markdown format")] string planContent,
            CancellationToken cancellationToken)
        {
            var execCtx = chat.ExecutionContext;
            if (execCtx == null)
                return "No execution context available — cannot determine thread path.";

            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var planNode = new MeshNode("Plan", execCtx.ThreadPath)
            {
                Name = "Execution Plan",
                NodeType = "Markdown",
                Content = planContent
            };

            await meshService.CreateNodeAsync(planNode, cancellationToken);
            return $"Plan stored at {execCtx.ThreadPath}/Plan";
        }

        return AIFunctionFactory.Create(
            StorePlan,
            name: "store_plan",
            description: "Stores the execution plan as a Markdown node under the current thread. Use this to persist your plan for future reference and debugging.");
    }
}
