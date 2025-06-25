using MeshWeaver.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Documentation.AI;

/// <summary>
/// Extension methods for adding Documentation AI services
/// </summary>
public static class DocumentationAIExtensions
{
    /// <summary>
    /// Adds Documentation AI services including the TicTacToe agents
    /// </summary>
    public static IServiceCollection AddDocumentationAI(this IServiceCollection services)
    {
        return services
            .AddSingleton<IAgentDefinition, TicTacToePlayer1>()
            .AddSingleton<IAgentDefinition, TicTacToePlayer2>();
    }
}
