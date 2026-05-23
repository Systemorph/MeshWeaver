using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Constants and configuration for Thread node types.
/// </summary>
public static class ThreadNodeType
{
    /// <summary>
    /// The NodeType value used to identify thread nodes.
    /// </summary>
    public const string NodeType = "Thread";

    /// <summary>
    /// Default Icon for Thread instances. Must match <see cref="CreateMeshNode"/>.
    /// Applied explicitly in <see cref="BuildThreadNode"/>/<see cref="BuildThreadWithMessages"/>
    /// because thread creation goes via <c>CreateNodeRequest</c> on arbitrary parent hubs,
    /// some of which don't have <c>INodeTypeService</c> registered to auto-copy the icon.
    /// </summary>
    public const string DefaultIcon = "/static/NodeTypeIcons/chat.svg";

    /// <summary>
    /// Satellite partition name for threads (like _Comment for comments).
    /// Threads are created at {contextPath}/_Thread/{speakingId}.
    /// </summary>
    public const string ThreadPartition = "_Thread";

    /// <summary>
    /// Layout area for thread content and message history (default).
    /// </summary>
    public const string ThreadArea = "Thread";

    /// <summary>
    /// Layout area for just the chat control (messages + input), without the header.
    /// Used by the side panel to avoid rendering the full-page header.
    /// </summary>
    public const string ThreadChatArea = "ThreadChat";

    /// <summary>
    /// Layout area showing current execution progress: streaming response message.
    /// Parent threads subscribe to this.
    /// </summary>
    public const string StreamingArea = "Streaming";

    /// <summary>
    /// Layout area for delegation sub-thread history.
    /// </summary>
    public const string HistoryArea = "History";

    /// <summary>
    /// Layout area shown above the chat: parent-thread origin link (when this thread
    /// is a delegation), aggregated list of nodes modified by this thread's execution
    /// with version-before / version-after, and click-through to the version compare view.
    /// </summary>
    public const string HeaderArea = "Header";

    /// <summary>
    /// Layout area showing the aggregated list of nodes modified across every
    /// message of the thread, with version-before / version-after, Diff link,
    /// per-row Revert, and a bulk "Revert All" action. Surfaced through the
    /// node menu (Changes).
    /// </summary>
    public const string ChangesArea = "Changes";

    /// <summary>
    /// Generates a human-readable speaking ID from message text.
    /// Takes the first few words, lowercases, replaces non-alphanumeric with hyphens,
    /// and appends a short unique suffix.
    /// Example: "Hello, can you help me with this?" → "hello-can-you-help-me-a3f9"
    /// </summary>
    public static string GenerateSpeakingId(string messageText)
    {
        // Take first ~50 chars, lowercase, replace non-alphanumeric with hyphens
        var slug = messageText.Length > 50 ? messageText[..50] : messageText;
        slug = Regex.Replace(slug.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

        // Truncate to reasonable length
        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('-');

        // Append short unique suffix to avoid collisions
        var suffix = Guid.NewGuid().ToString("N")[..4];
        return string.IsNullOrEmpty(slug) ? suffix : $"{slug}-{suffix}";
    }

    /// <summary>
    /// Builds a MeshNode for a new thread. Use with CreateNodeRequest.
    /// </summary>
    /// <param name="contextPath">The namespace/context path (e.g., "User/Roland")</param>
    /// <param name="messageText">First message text — used for name and speaking ID</param>
    /// <param name="createdBy">User ID who creates the thread</param>
    public static MeshNode BuildThreadNode(string contextPath, string messageText, string? createdBy = null)
    {
        var speakingId = GenerateSpeakingId(messageText);
        // Add _Thread partition for top-level threads. Sub-threads (delegations)
        // live directly under the parent response message — no nested _Thread.
        var ns = string.IsNullOrEmpty(contextPath)
            ? ThreadPartition
            : contextPath.Contains($"/{ThreadPartition}/")
                ? contextPath
                : $"{contextPath}/{ThreadPartition}";
        var name = messageText.Length > 60
            ? messageText[..57] + "..."
            : messageText;

        return new MeshNode(speakingId, ns)
        {
            Name = name,
            NodeType = NodeType,
            Icon = DefaultIcon,
            MainNode = contextPath,
            Content = new Thread { CreatedBy = createdBy }
        };
    }

    /// <summary>
    /// Builds a thread node with pre-populated messages and pending execution.
    /// When the thread grain activates, it auto-starts execution — no separate SubmitMessageRequest needed.
    /// </summary>
    public static (MeshNode Thread, string UserMsgId, string ResponseMsgId) BuildThreadWithMessages(
        string contextPath, string messageText,
        string? createdBy = null, string? agentName = null,
        string? modelName = null, IReadOnlyList<string>? attachments = null)
    {
        var speakingId = GenerateSpeakingId(messageText);
        // Add _Thread partition for top-level threads. Sub-threads (delegations)
        // live directly under the parent response message — no nested _Thread.
        var ns = string.IsNullOrEmpty(contextPath)
            ? ThreadPartition
            : contextPath.Contains($"/{ThreadPartition}/")
                ? contextPath
                : $"{contextPath}/{ThreadPartition}";
        var name = messageText.Length > 60
            ? messageText[..57] + "..."
            : messageText;

        var userMsgId = Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = Guid.NewGuid().ToString("N")[..8];

        var threadNode = new MeshNode(speakingId, ns)
        {
            Name = name,
            NodeType = NodeType,
            Icon = DefaultIcon,
            MainNode = contextPath,
            Content = new Thread
            {
                CreatedBy = createdBy,
                Messages = ImmutableList.Create(userMsgId, responseMsgId),
                Status = ThreadExecutionStatus.Executing,
                ActiveMessageId = responseMsgId,
                ExecutionStartedAt = DateTime.UtcNow,
                PendingUserMessage = messageText,
                PendingAgentName = agentName,
                PendingModelName = modelName,
                PendingContextPath = contextPath,
                PendingAttachments = attachments
            }
        };

        return (threadNode, userMsgId, responseMsgId);
    }

    /// <summary>
    /// Checks if a MeshNode is a Thread by checking its NodeType.
    /// </summary>
    /// <param name="nodeType">The node type to check.</param>
    /// <returns>True if the node type is Thread.</returns>
    public static bool IsThreadNodeType(string? nodeType)
    {
        return string.Equals(nodeType, NodeType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers the built-in "Thread" MeshNode on the mesh builder.
    /// </summary>
    /// <param name="builder">The mesh builder.</param>
    /// <param name="hubConfiguration">Hub configuration for thread nodes (views, data sources, etc.).</param>
    public static TBuilder AddThreadType<TBuilder>(this TBuilder builder,
        Func<MessageHubConfiguration, MessageHubConfiguration>? hubConfiguration = null)
        where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode(hubConfiguration));
        builder.AddAutocompleteExcludedTypes(NodeType);
        // Public-read on the Thread NodeType HOST hub (address = "Thread") —
        // grants any authenticated user Read on the type's shared metadata
        // (layout definitions, schema). This is the type DEFINITION, not the
        // per-instance thread data — instance access is gated by RLS on the
        // node's mainNode/path separately. Without this, per-instance Thread
        // hubs can't subscribe to their type's MeshNodeReference at activation,
        // surfacing as "Access denied: user '<thread-instance-path>' lacks
        // Read permission on 'Thread'" and the chat view never loads. Matches
        // Agent, User, Code, Markdown, etc.
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        // Per-instance access: Thread is a satellite — Read requires Read on
        // the conversation's MainNode, Create/Update/Delete require Update
        // on the MainNode. Matches Comment / Activity / TrackedChange.
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new MeshWeaver.Graph.Security.SatelliteAccessRule(
                    NodeType,
                    sp.GetService<ISecurityService>() ?? new NullSecurityService()));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Thread node type.
    /// </summary>
    /// <param name="hubConfiguration">Hub configuration for thread nodes (views, data sources, etc.).</param>
    public static MeshNode CreateMeshNode(
        Func<MessageHubConfiguration, MessageHubConfiguration>? hubConfiguration = null) => new(NodeType)
        {
            Name = "Thread",
            Icon = DefaultIcon,
            IsSatelliteType = true,
            ExcludeFromContext = ImmutableHashSet.Create("search"),
            // Register AI types DIRECTLY on the per-thread hub config — not just
            // via ConfigureDefaultNodeHub. The polymorphic resolver discriminator
            // is picked from the SENDING hub's TypeRegistry; if Thread NodeType's
            // HubConfiguration runs in isolation (a test or host that didn't wire
            // ConfigureDefaultNodeHub), unregistered types fall back to FullName
            // on the wire and the receiver (whose registry has the short name)
            // can't resolve $type → DeliveryFailure on every response.
            // See Doc/Architecture/DebuggingMessageFlow.md "Watch for FQN vs
            // short-name mismatches".
            HubConfiguration = config =>
            {
                config.TypeRegistry.AddAITypes();
                return config
                    .AddThreadLayoutAreas()
                    .AddThreadExecution()
                    .AddMeshDataSource(source => source
                        .WithContentType<Thread>());
            }
        };
}
