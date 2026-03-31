using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.ClaudeCode;

/// <summary>
/// Extension methods for adding Claude Code (Claude Agent SDK) services.
/// </summary>
public static class ClaudeCodeExtensions
{
    /// <summary>
    /// Adds Claude Code services to the service collection.
    /// Configuration should be bound to ClaudeCodeConfiguration.
    /// Requires Claude Code CLI >= 2.0.0 installed via: npm install -g @anthropic-ai/claude-code
    /// </summary>
    public static IServiceCollection AddClaudeCode(this IServiceCollection services)
    {
        return services.AddSingleton<IChatClientFactory, ClaudeCodeChatClientAgentFactory>();
    }

    /// <summary>
    /// Adds Claude Code services with configuration action.
    /// Requires Claude Code CLI >= 2.0.0 installed via: npm install -g @anthropic-ai/claude-code
    /// </summary>
    public static IServiceCollection AddClaudeCode(
        this IServiceCollection services,
        Action<ClaudeCodeConfiguration> configure)
    {
        services.Configure(configure);
        return services.AddSingleton<IChatClientFactory, ClaudeCodeChatClientAgentFactory>();
    }
}
