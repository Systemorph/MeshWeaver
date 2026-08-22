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
        Task<string> StorePlan(
            [Description("The plan content in Markdown format")] string planContent,
            CancellationToken cancellationToken)
        {
            var execCtx = chat.ExecutionContext;
            if (execCtx == null)
                return Task.FromResult("No execution context available — cannot determine thread path.");

            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var planNode = new MeshNode("Plan", execCtx.ThreadPath)
            {
                Name = "Execution Plan",
                NodeType = "Markdown",
                MainNode = execCtx.ContextPath ?? execCtx.ThreadPath,
                Content = planContent
            };

            // Use IObservable CreateNode — no await, no deadlock. Every terminal settles, and the
            // round's token is observed: before #1956 this was a bare TaskCompletionSource with a
            // 2-arg Subscribe, so an empty completion left the task pending and Stop no-opped on a
            // create that was merely slow (bounded only by the hub's 30 s RequestTimeout, during
            // which the round holds its Ai-pool gate permit).
            return ToolTask.Bridge(
                meshService.CreateNode(planNode),
                cancellationToken,
                _ => $"Plan stored at {execCtx.ThreadPath}/Plan",
                ex => $"Error storing plan: {ex.Message}",
                () => $"Plan was not stored at {execCtx.ThreadPath}/Plan — the create completed without confirming the node.");
        }

        return AIFunctionFactory.Create(
            StorePlan,
            name: "store_plan",
            description: "Stores the execution plan as a Markdown node under the current thread. Use this to persist your plan for future reference and debugging.");
    }
}
