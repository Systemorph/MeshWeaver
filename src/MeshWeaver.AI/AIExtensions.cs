using System.Reactive.Linq;
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
        public TBuilder AddAI(IReadOnlySet<string>? serveFromPartition = null)
        {
            // Register AI types in type registry and chat services. serveFromPartition lists the
            // partitions (e.g. "Agent", "Model") whose static content is DB-synced (static-repo
            // import) — for those, the read-only in-memory partition provider is skipped so
            // Postgres serves them. Null/empty = in-memory serving (current behaviour).
            return (TBuilder)builder
                    .AddThreadMessageType()
                    .AddThreadType()
                    .AddTokenUsageType()
                    .AddAgentType(serveFromPartition)
                    .AddLanguageModelType(serveFromPartition)
                    .AddHarnessType(serveFromPartition)
                    .AddSkillType(serveFromPartition)
                    .AddThreadComposerType()
                    .AddAiSettingsType()
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
            .WithType(typeof(Harness), nameof(Harness))
            .WithType(typeof(AgentDelegation), nameof(AgentDelegation))
            .WithType(typeof(ModelDefinition), nameof(ModelDefinition))
            .WithType(typeof(ModelProviderConfiguration), nameof(ModelProviderConfiguration))
            .WithType(typeof(AI.Thread), nameof(AI.Thread))
            .WithType(typeof(ThreadMessage), nameof(ThreadMessage))
            // Per-(thread, model) token usage satellite at {threadPath}/_Usage/{model}. The thread
            // node carries NO token state — usage lives here. Registered mesh-wide so the node
            // serialises across routing / mesh / per-node hubs.
            .WithType(typeof(TokenUsage), nameof(TokenUsage))
            // ThreadComposer: content of the per-user {user}/_Thread/ThreadComposer composer
            // singleton (message text + harness/agent/model + attachments). Registered
            // mesh-wide so the node serialises across routing/mesh hubs.
            .WithType(typeof(ThreadComposer), nameof(ThreadComposer))
            // AiSettings: per-user {user}/_Memex/AiSettings config (enabled harnesses + agent/model
            // picker query templates). Registered mesh-wide so the node serialises across hubs.
            .WithType(typeof(AiSettings), nameof(AiSettings))
            // MessageViewModel is not registered — handled as JsonElement on the wire.
            // SubmitMessageRequest / SubmitMessageResponse deleted 2026-05-25:
            // the only mutation API is workspace.GetMeshNodeStream(path).Update(...).
            // Public submission flow is ThreadSubmission.Submit → ThreadInput.AppendUserInput
            // (writes PendingUserMessages); the submission watcher reacts and invokes
            // ExecuteMessageAsync directly as a method (no wire message).
            // See AGENTS.md → "GetMeshNodeStream().Update() is the ONLY mutation API"
            // and Doc/Architecture/RequestViaStreamUpdate.md.
            // Thread mutation triggers and intent payloads (ResubmitIntent,
            // FailureRecord, RequestedResubmit / RequestedDeleteFromMessageId /
            // PendingFailures fields) were deleted — HubThreadExtensions does the
            // full mutation inline via a single stream.Update on the thread node.
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
            services.AddTransient<IIconGenerator, IconGenerator>();
            services.AddTransient<IDescriptionGenerator, DescriptionGenerator>();

            // Slash-skills are declarative nodeType:Skill mesh nodes (BuiltInSkillProvider, imported to
            // PG), extensible per Space/NodeType/user via namespace inheritance — there is no C# command
            // registry. See SkillNodeType / SkillAutocompleteProvider.

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
