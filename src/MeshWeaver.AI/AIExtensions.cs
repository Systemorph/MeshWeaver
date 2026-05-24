using System.Reactive.Linq;
using MeshWeaver.AI.Commands;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
                    .AddLanguageModelType()
                    .ConfigureServices(services => services.AddAgentChatServices())
                    // Register AI types on the MESH hub (for MeshQuery deserialization of Thread content)
                    .ConfigureHub(config =>
                    {
                        config.TypeRegistry.AddAITypes();
                        return config;
                    })
                    .ConfigureDefaultNodeHub(config =>
                    {
                        config.TypeRegistry.AddAITypes();
                        return config
                            .WithHandler<Plugins.SaveContentRequest>(HandleSaveContent);
                    })
                ;
        }
    }

    private static IMessageDelivery HandleSaveContent(
        IMessageHub hub, IMessageDelivery<Plugins.SaveContentRequest> delivery)
    {
        var request = delivery.Message;
        var fileProvider = hub.ServiceProvider.GetService<IFileContentProvider>();

        if (fileProvider == null)
        {
            hub.Post(new Plugins.SaveContentResponse { Success = false, Error = "Content collections not configured on this node" },
                o => o.ResponseFor(delivery));
            return delivery.Processed();
        }

        var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.TextContent));
        fileProvider.SaveFileContent(request.CollectionName, request.FilePath, stream)
            .Subscribe(
                result => hub.Post(
                    new Plugins.SaveContentResponse { Success = result.Success, Error = result.Error },
                    o => o.ResponseFor(delivery)),
                ex => hub.Post(
                    new Plugins.SaveContentResponse { Success = false, Error = ex.Message },
                    o => o.ResponseFor(delivery)));

        return delivery.Processed();
    }

    public static ITypeRegistry AddAITypes(this ITypeRegistry typeRegistry)
        => typeRegistry.WithType(typeof(AgentConfiguration), nameof(AgentConfiguration))
            .WithType(typeof(AgentDelegation), nameof(AgentDelegation))
            .WithType(typeof(ModelDefinition), nameof(ModelDefinition))
            .WithType(typeof(ModelProviderConfiguration), nameof(ModelProviderConfiguration))
            .WithType(typeof(AI.Thread), nameof(AI.Thread))
            .WithType(typeof(ThreadMessage), nameof(ThreadMessage))
            // MessageViewModel is not registered — handled as JsonElement on the wire.
            // SubmitMessageRequest is the LAST surviving thread-mutation request —
            // its handler pre-allocates a CancellationTokenSource that the
            // stream-update path can't replicate without a side-effect watcher.
            // Migration plan tracked in tasks #10. Everything else
            // (Append/Resubmit/Cancel/Delete) was migrated to stream.Update via
            // ThreadInput / ThreadSubmission helpers and DELETED.
            // See Doc/Architecture/RequestViaStreamUpdate.md.
            .WithType(typeof(SubmitMessageRequest), nameof(SubmitMessageRequest))
            .WithType(typeof(SubmitMessageResponse), nameof(SubmitMessageResponse))
            // Internal triggers — registered on the wire so a remote client's
            // ThreadSubmission.Apply* call can land on the per-thread hub.
            .WithType(typeof(ResubmitTrigger), nameof(ResubmitTrigger))
            .WithType(typeof(DeleteFromMessageTrigger), nameof(DeleteFromMessageTrigger))
            .WithType(typeof(RecordSubmissionFailureTrigger), nameof(RecordSubmissionFailureTrigger))
            .WithType(typeof(StartExecutionTrigger), nameof(StartExecutionTrigger))
            .WithType(typeof(ThreadMutationAck), nameof(ThreadMutationAck))
            .WithType(typeof(ToolCallEntry), nameof(ToolCallEntry))
            .WithType(typeof(NodeChangeEntry), nameof(NodeChangeEntry))
            .WithType(typeof(ThreadExecutionContext), nameof(ThreadExecutionContext))
            // ChatHistoryEntry removed — ChatHistory uses string[] to avoid $type issues
            .WithType(typeof(SaveContentRequest), nameof(SaveContentRequest))
            .WithType(typeof(SaveContentResponse), nameof(SaveContentResponse))
            // Delegation heartbeat: parent-thread-hub-scoped messages for
            // hung-sub-thread detection + cancel propagation.
            .WithType(typeof(Delegation.HeartbeatTick), nameof(Delegation.HeartbeatTick))
            .WithType(typeof(Delegation.CancelDelegationSubThread), nameof(Delegation.CancelDelegationSubThread));

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds the AI chat services including persistence and thread management.
        /// Uses in-memory thread manager by default.
        /// Call this after registering individual factory implementations (e.g., AddAzureOpenAI, AddAzureFoundryClaude).
        /// </summary>
        public IServiceCollection AddAgentChatServices()
        {
            services.AddOptions<ModelTierConfiguration>()
                .BindConfiguration("ModelTier");
            services.AddTransient<IIconGenerator, IconGenerator>();
            services.AddTransient<IDescriptionGenerator, DescriptionGenerator>();

            // Slash-command infrastructure: each IChatCommand is an
            // independent class; the registry composes them. Registered as
            // singletons so registration is idempotent across multiple
            // AddAgentChatServices() calls (mesh hub + portal hub both
            // configure AI). Help.HelpCommand resolves the registry lazily
            // to break the constructor cycle.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatCommand, AgentCommand>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatCommand, ModelCommand>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatCommand, HelpCommand>());
            services.TryAddSingleton<ChatCommandRegistry>(sp =>
            {
                var registry = new ChatCommandRegistry(
                    sp.GetService<ILoggerFactory>()?.CreateLogger<ChatCommandRegistry>());
                foreach (var cmd in sp.GetServices<IChatCommand>())
                    registry.Register(cmd);
                return registry;
            });

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
