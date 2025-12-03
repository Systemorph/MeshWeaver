using MeshWeaver.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Todo.AI;

/// <summary>
/// Extension methods for adding Northwind AI services
/// </summary>
public static class TodoAIExtensions
{
    /// <summary>
    /// Adds Northwind AI services including the NorthwindAgent
    /// </summary>
    public static IServiceCollection AddTodoAI(this IServiceCollection services)
    {
        return services
            .AddSingleton<IAgentDefinition, TodoAgent>();
    }
}
