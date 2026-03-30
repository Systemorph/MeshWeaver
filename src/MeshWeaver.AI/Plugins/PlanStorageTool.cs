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

            // Use IObservable CreateNode — no await, no deadlock
            var tcs = new TaskCompletionSource<string>();
            meshService.CreateNode(planNode).Subscribe(
                _ => tcs.TrySetResult($"Plan stored at {execCtx.ThreadPath}/Plan"),
                ex => tcs.TrySetResult($"Error storing plan: {ex.Message}"));
            return tcs.Task;
        }

        return AIFunctionFactory.Create(
            StorePlan,
            name: "store_plan",
            description: "Stores the execution plan as a Markdown node under the current thread. Use this to persist your plan for future reference and debugging.");
    }
}
