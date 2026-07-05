using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// Default Icon for the Thread <em>type</em> node (<see cref="CreateMeshNode"/>).
    /// Per-<em>instance</em> threads instead get a deterministic identicon from
    /// <see cref="ThreadIconGenerator"/> in <see cref="BuildThreadNode"/>/<see cref="BuildThreadWithMessages"/>
    /// so the catalog renders a distinct visual per thread; this glyph is the fallback for
    /// the type itself and any consumer that reads the type default.
    /// </summary>
    public const string DefaultIcon = "/static/NodeTypeIcons/chat.svg";

    /// <summary>
    /// Satellite partition name for threads (like _Comment for comments).
    /// Threads are created at {contextPath}/_Thread/{speakingId}.
    /// </summary>
    public const string ThreadPartition = "_Thread";

    /// <summary>
    /// The MAIN NODE of a path: everything before the first <c>/_Thread</c> segment — i.e.
    /// "what the chat is about". <c>rbuergi/_Thread/x</c> → <c>rbuergi</c>;
    /// <c>acme/Project/X/_Thread/y/ThreadComposer</c> → <c>acme/Project/X</c>; a path with no
    /// <c>_Thread</c> returns unchanged. Stripping at the FIRST <c>_Thread</c> collapses any
    /// accidental nesting (a thread/composer under another thread) back to the true main node,
    /// so the per-(node,user) ThreadComposer never nests.
    /// </summary>
    public static string MainNodeOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var idx = path.IndexOf($"/{ThreadPartition}", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? path[..idx] : path;
    }

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
    /// Layout area for the full-page thread hero header (gradient title block, context
    /// back-link, live message-count subtitle, Mark Done toggle). Rendered INSIDE the
    /// scrollable message area by the full-page chat view so it scrolls away with the
    /// conversation rather than staying pinned above it.
    /// </summary>
    public const string FullHeaderArea = "FullHeader";

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
    /// <param name="speakingId">Optional pre-chosen speaking ID for the thread node; when null one is generated from <paramref name="messageText"/>.</param>
    public static MeshNode BuildThreadNode(string contextPath, string messageText, string? createdBy = null,
        string? speakingId = null)
    {
        speakingId ??= GenerateSpeakingId(messageText);
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
            // Deterministic per-thread identicon (seeded by the stable speaking id) so the
            // thread catalog renders a distinct visual per thread instead of one shared glyph.
            // Same id ⇒ same icon (the fallback re-anchor below reuses the id and gets the same
            // icon); the value is inline <svg>, which the node card renders directly.
            Icon = ThreadIconGenerator.Generate(speakingId),
            MainNode = contextPath,
            Content = new Thread { CreatedBy = createdBy }
        };
    }

    /// <summary>
    /// Builds a thread node pre-seeded with a user message in
    /// <see cref="Thread.PendingUserMessages"/>. The thread starts at
    /// <see cref="ThreadExecutionStatus.Idle"/>; when the per-thread hub
    /// activates, <c>InstallServerWatcher</c> sees the pending entry and
    /// drives the standard claim → DispatchRound → execute flow. No
    /// pre-allocated satellite cells, no <c>ActiveMessageId</c>, no
    /// auto-execute trigger competing with the submission watcher.
    ///
    /// <para>The returned <c>UserMsgId</c> is the key used in
    /// <c>PendingUserMessages</c>. <c>ResponseMsgId</c> is intentionally
    /// the empty string for back-compat: <see cref="ThreadSubmissionServer.DispatchAfterClaim"/>
    /// allocates the real response cell id when the watcher claims the
    /// round. Callers that need the response id after dispatch should
    /// subscribe to <see cref="Thread.ActiveMessageId"/>.</para>
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
        var userMessage = new ThreadMessage
        {
            Role = "user",
            Text = messageText,
            CreatedBy = createdBy,
            AgentName = agentName,
            ModelName = modelName,
            ContextPath = contextPath,
            Attachments = attachments,
            Timestamp = DateTime.UtcNow,
            Type = ThreadMessageType.ExecutedInput,
            Status = ThreadMessageStatus.Submitted
        };

        var threadNode = new MeshNode(speakingId, ns)
        {
            Name = name,
            NodeType = NodeType,
            // Deterministic per-thread identicon (see BuildThreadNode) so the catalog shows a
            // distinct visual per thread; inline <svg>, rendered directly by the node card.
            Icon = ThreadIconGenerator.Generate(speakingId),
            MainNode = contextPath,
            Content = new Thread
            {
                CreatedBy = createdBy,
                Status = ThreadExecutionStatus.Idle,
                UserMessageIds = ImmutableList.Create(userMsgId),
                // The pending ThreadMessage carries the round's selection
                // (agent/model/context/attachments) — no thread-level Pending* mirror.
                PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                    .Add(userMsgId, userMessage)
            }
        };

        // ResponseMsgId is now allocated by DispatchAfterClaim, not here. Return
        // empty string for back-compat — call sites that wanted the id pre-emptively
        // (e.g. for parent tool-call tracking) should read Thread.ActiveMessageId
        // from the stream after the submission watcher claims.
        return (threadNode, userMsgId, "");
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
                    sp.GetRequiredService<IMessageHub>()));
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
            // The NodeType carries the GUI-create protocol: creating a Thread from the "+"
            // (anywhere) opens the new-chat composer (the per-user ThreadComposer / new-thread
            // view) and creates nothing up front — the thread is created on submit, NOT via
            // the generic Create form. Injected as BuildCreate so the generic CreateLayoutArea
            // delegates to us regardless of which "+" routed here.
            Content = new MeshWeaver.Graph.Configuration.NodeTypeDefinition
            {
                BuildCreate = (host, ns) =>
                {
                    // Composer node, owned per (main node, user). The MAIN NODE is the path
                    // before /_Thread (what the chat is ABOUT); the user is the signed-in
                    // identity. For the user's own partition this is the per-user default
                    // {user}/_Thread/ThreadComposer (seeded at onboarding); for any other node it's
                    // {node}/_Thread/{user}/ThreadComposer. Always under _Thread with ThreadComposer as the
                    // leaf — NEVER the bare {node}/_Thread/ThreadComposer, which doesn't read back.
                    var mainNode = MainNodeOf(ns);
                    var accessSvc = host.Hub.ServiceProvider.GetService<AccessService>();
                    var user = accessSvc?.Context?.ObjectId ?? accessSvc?.CircuitContext?.ObjectId;
                    var chatInputPath =
                        string.IsNullOrEmpty(user) || string.Equals(mainNode, user, StringComparison.OrdinalIgnoreCase)
                            ? ThreadComposerNodeType.PathFor(string.IsNullOrEmpty(user) ? mainNode : user)
                            : ThreadComposerNodeType.PathForNode(mainNode, user);
                    var overviewHref = $"/{chatInputPath}";
                    var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
                    var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger("MeshWeaver.AI.ThreadCreate");
                    var node = MeshNode.FromPath(chatInputPath) with
                    {
                        Name = "New Chat",
                        NodeType = ThreadComposerNodeType.NodeType,
                        MainNode = mainNode,
                        State = MeshNodeState.Active,
                    };
                    // Create then navigate. A real failure (anything other than "already exists")
                    // leaves the composer overview empty — surface it in the log instead of
                    // silently swallowing; we still navigate so an existing composer opens.
                    return meshService.CreateNode(node)
                        .Take(1)
                        .Select(_ => (UiControl?)new RedirectControl(overviewHref))
                        .Catch<UiControl?, Exception>(ex =>
                        {
                            logger?.LogWarning(ex,
                                "[ThreadCreate] ThreadComposer create failed at {Path}; navigating to overview anyway",
                                chatInputPath);
                            return Observable.Return<UiControl?>(new RedirectControl(overviewHref));
                        });
                }
            },
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
