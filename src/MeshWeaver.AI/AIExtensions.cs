using MeshWeaver.AI.Persistence;
using MeshWeaver.AI.Plugins;
using MeshWeaver.AI.Threading;
using MeshWeaver.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Extension methods for AI services
/// </summary>
public static class AIExtensions
{
    extension<TBuilder>(TBuilder builder)
    where TBuilder : MeshBuilder
    {
        public TBuilder AddAI()
        {
            // Register AI types in type registry and chat services
            return (TBuilder)builder
                    .AddThreadMessageType()
                    .AddThreadType()
                    .AddAgentType()
                    .ConfigureServices(services => services.AddAgentChatServices())
                    .ConfigureDefaultNodeHub(config => config.AddThreadSupport())
                ;
        }
    }

    public static ITypeRegistry AddAITypes(this ITypeRegistry typeRegistry)
        => typeRegistry.WithType(typeof(AgentConfiguration), nameof(AgentConfiguration))
            .WithType(typeof(AgentDelegation), nameof(AgentDelegation))
            .WithType(typeof(AI.Thread), nameof(AI.Thread))
            .WithType(typeof(ThreadMessage), nameof(ThreadMessage))
            // MessageViewModel is not registered — handled as JsonElement on the wire
            .WithType(typeof(SubmitMessageRequest), nameof(SubmitMessageRequest))
            .WithType(typeof(SubmitMessageResponse), nameof(SubmitMessageResponse))
.WithType(typeof(CancelThreadStreamRequest), nameof(CancelThreadStreamRequest))
            .WithType(typeof(ResubmitMessageRequest), nameof(ResubmitMessageRequest))
            .WithType(typeof(DeleteFromMessageRequest), nameof(DeleteFromMessageRequest))
            .WithType(typeof(EditMessageRequest), nameof(EditMessageRequest));

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds the AI chat services including persistence and thread management.
        /// Uses in-memory thread manager by default.
        /// Call this after registering individual factory implementations (e.g., AddAzureOpenAI, AddAzureFoundryClaude).
        /// </summary>
        public IServiceCollection AddAgentChatServices()
        {
            services.AddMemoryChatPersistence();
            return services;
        }


        /// <summary>
        /// Backwards-compatible method - same as AddAgentChatServices.
        /// </summary>
        [Obsolete("Use AddAgentChatServices instead")]
        public IServiceCollection AddAgentChatFactoryProvider()
        {
            return services.AddAgentChatServices();
        }

        /// <summary>
        /// Registers the WebSearch plugin, making SearchWeb and FetchWebPage tools
        /// available to agents that declare "WebSearch" in their plugins frontmatter.
        /// </summary>
        public IServiceCollection AddWebSearchPlugin(Action<WebSearchConfiguration>? configure = null)
        {
            if (configure != null)
                services.Configure(configure);
            else
                services.AddOptions<WebSearchConfiguration>();

            services.AddHttpClient<WebSearchPlugin>();
            services.AddSingleton<IAgentPlugin, WebSearchPlugin>();
            return services;
        }
    }
}
