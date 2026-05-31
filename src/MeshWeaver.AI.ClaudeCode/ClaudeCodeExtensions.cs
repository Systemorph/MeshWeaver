using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.ClaudeCode;

/// <summary>
/// Extension methods for adding Claude Code (Claude Agent SDK) services.
/// </summary>
public static class ClaudeCodeExtensions
{
    /// <summary>
    /// Registers the Claude Code provider's catalog profile so its models
    /// (sonnet / opus / haiku) surface in the chat picker + Models settings
    /// tab. The provider's "key" is the user's subscription OAuth token
    /// (<c>RequiresApiKey: true</c>) — stored encrypted like any other key
    /// (Phase 2) and injected per request into the worker (Phase 5b). Pair
    /// with <see cref="AddClaudeCode(IServiceCollection, Action{ClaudeCodeConfiguration})"/>
    /// which registers the factory + binds <see cref="ClaudeCodeConfiguration"/>.
    /// </summary>
    public static TBuilder AddClaudeCode<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
            SectionName: "ClaudeCode",
            ProviderName: "ClaudeCode",
            Order: 5,
            DisplayLabel: "Claude Code (my subscription)",
            DefaultEndpoint: null,
            DefaultModelIds: ImmutableArray.Create("sonnet", "opus", "haiku"),
            RequiresApiKey: true));
        return builder;
    }

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
