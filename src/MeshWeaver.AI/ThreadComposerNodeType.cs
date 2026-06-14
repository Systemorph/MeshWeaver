using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Per-user <b>ThreadComposer</b> node — the persistent state of a user's chat input box
/// (draft text + the selected harness / agent / model). A per-user <b>singleton</b> stored at
/// <c>{userHome}/_Thread/ThreadComposer</c> as a <b>satellite</b> node
/// (<see cref="MeshNode.IsSatelliteType"/> = <see langword="true"/> → the partition's
/// <c>threads</c> table, alongside <c>Thread</c> / <c>ThreadMessage</c>).
///
/// <para><b>Why under <c>_Thread</c>, and why it must be a registered satellite:</b> the composer
/// is thread-family state, kept in the <c>_Thread</c> partition consistently with the per-node
/// composer (<see cref="PathForNode"/>). For the read to find it, <c>ThreadComposer</c> MUST resolve
/// to the SAME table by BOTH its path segment (<c>_Thread</c> → <c>threads</c>) and its nodeType —
/// so it is registered in <see cref="SatelliteTableMapping"/> as a <c>_Thread</c>/<c>threads</c>
/// nodeType. Without that nodeType mapping, the write routed to <c>threads</c> (by path) but the
/// nodeType resolved to <c>mesh_nodes</c> → the single-node read missed the row → routing
/// <c>NotFound</c> → the composer's bound layout-area <c>SynchronizationStream</c> OnErrored and the
/// input box vanished (the 2026-06-10 "ThreadComposer disappears on model-select" bug).</para>
///
/// <para><b>Why a singleton, seeded at onboarding:</b> the node is materialized for every user when
/// their <c>User</c> partition is created (<see cref="ThreadComposerSeedHandler"/>, a
/// <see cref="INodePostCreationHandler"/>), so the chat composer's read always RESOLVES instead of
/// generating a routing NotFound that the GUI re-issues on a loop — the 2026-06-08 event-storm
/// class. The content is the dedicated <see cref="ThreadComposer"/> record (message text + the
/// harness/agent/model comboboxes + attachments) — exactly the fields the out-of-thread composer
/// owns, not the full conversation <see cref="Thread"/> shape.</para>
///
/// <para><b>🚨 Where the composer lives — the rule:</b> the standalone <c>ThreadComposer</c> node
/// (this singleton, in the user's home under <c>_Thread</c>) is used ONLY when there is NO thread yet
/// — the new-chat composer. The moment a thread exists, the composer is the thread's INLINE
/// <see cref="Thread.Composer"/> object on the thread node ITSELF; the thread always refers to its own
/// embedded composer object, NEVER an outside composer node. <see cref="ComposerOf"/> /
/// <c>WithComposer</c> read/write whichever inline shape applies (discriminated by NodeType), and the
/// GUI binds DIRECTLY to that inline location — see <c>ThreadComposerView.ComposerContext</c>.</para>
/// </summary>
public static class ThreadComposerNodeType
{
    /// <summary>NodeType discriminator AND the singleton instance id for the per-user chat-input state node.</summary>
    public const string NodeType = "ThreadComposer";

    /// <summary>
    /// The user partition's hidden Memex-defaults namespace segment (a dotfile), <c>_Memex</c> — a
    /// non-satellite namespace for per-user defaults (e.g. <c>ModelProvider</c>). Hidden from search by
    /// <see cref="MeshNodeVisibility"/>. NOTE: the ThreadComposer singleton itself does NOT live here —
    /// it lives under <c>_Thread</c> (see <see cref="PathFor"/>); this const is retained for the other
    /// per-user defaults that DO use <c>_Memex</c>.
    /// </summary>
    public const string MemexDefaultsNamespace = "_Memex";

    /// <summary>Registers the ThreadComposer type node and the per-user singleton seed handler.</summary>
    public static TBuilder AddThreadComposerType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureServices(services =>
        {
            // Seed {user}/_Thread/ThreadComposer when a User partition is onboarded so the
            // composer's read always resolves (no read-before-create NotFound storm).
            services.AddSingleton<INodePostCreationHandler>(_ => new ThreadComposerSeedHandler());
            return services;
        });
        return builder;
    }

    /// <summary>The type-definition node for nodeType="ThreadComposer".</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Chat Input",
        Icon = "/static/NodeTypeIcons/message.svg",
        // Satellite node → the partition's `threads` table (registered in SatelliteTableMapping as a
        // `_Thread`/threads nodeType). Instances live under {user}/_Thread/ThreadComposer; both the
        // path segment (_Thread) and the nodeType resolve to `threads`, so write and read agree and
        // the selection persists. Hidden from the create menu and search so users never hand-create one.
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ThreadComposer>())
            // The composer is the node's default ("") layout area — see ThreadComposerView.
            .AddThreadComposerView()
    };

    /// <summary>
    /// The per-user default composer path: <c>{user}/_Thread/ThreadComposer</c> — the user's own
    /// new-chat composer (no specific node context). Kept under the user's <c>_Thread</c>
    /// partition for consistency with the per-node composer (<see cref="PathForNode"/>).
    /// <para>🚨 Read this path through a query (empty-on-absent), never a direct
    /// <c>GetMeshNodeStream</c> on the exact path — a maybe-absent direct read NotFound-storms
    /// the mesh (see feedback_optional_node_query_not_access). It is seeded at onboarding so the
    /// per-user one normally exists; per-node ones are created on first use.</para>
    /// </summary>
    public static string PathFor(string user) => $"{user}/{ThreadNodeType.ThreadPartition}/{NodeType}";

    /// <summary>
    /// The per-node, per-user composer path: <c>{node}/_Thread/{user}/ThreadComposer</c> — the
    /// composer state when a chat is started in the context of a specific node. Owned per
    /// (node, user). Structured with <c>{user}</c> as the owning segment under the node's
    /// <c>_Thread</c> partition and <c>ThreadComposer</c> as the leaf, so it reads back like a thread
    /// cell. Reads MUST go through a query, never a direct exact-path <c>GetMeshNodeStream</c>.
    /// </summary>
    public static string PathForNode(string node, string user) =>
        $"{node}/{ThreadNodeType.ThreadPartition}/{user}/{NodeType}";

    /// <summary>
    /// The composer state carried by <paramref name="node"/>, discriminated by
    /// <see cref="MeshNode.NodeType"/>: a <c>ThreadComposer</c> node's own content, or a
    /// <c>Thread</c> node's embedded <see cref="Thread.Composer"/> (empty composer when the
    /// thread predates the embed). Null for any other node or unreadable content
    /// (bad-data tolerant via <c>ContentAs</c> — never throws on a degraded JsonElement).
    /// </summary>
    public static ThreadComposer? ComposerOf(MeshNode? node, JsonSerializerOptions options, ILogger? logger = null)
        => node?.NodeType switch
        {
            NodeType => node.ContentAs<ThreadComposer>(options, logger),
            ThreadNodeType.NodeType => node.ContentAs<Thread>(options, logger) is { } thread
                ? thread.Composer ?? new ThreadComposer()
                : node.Content is null ? new ThreadComposer() : null,
            _ => null
        };

    /// <summary>
    /// Writes <paramref name="composer"/> back onto <paramref name="node"/> in the shape
    /// <see cref="ComposerOf"/> reads it from: whole content for a <c>ThreadComposer</c> node,
    /// <see cref="Thread.Composer"/> for a <c>Thread</c> node. An existing node whose content
    /// can't be recovered is left alone — NEVER clobbered.
    /// </summary>
    public static MeshNode WithComposer(MeshNode node, ThreadComposer composer, JsonSerializerOptions options, ILogger? logger = null)
    {
        if (node.NodeType == ThreadNodeType.NodeType)
        {
            var thread = node.ContentAs<Thread>(options, logger);
            if (node.Content is not null && thread is null)
                return node; // unreadable → leave alone
            return node with { Content = (thread ?? new Thread()) with { Composer = composer } };
        }
        if (node.Content is not null
            && node.ContentAs<ThreadComposer>(options, logger) is null)
            return node; // unreadable → leave alone
        return node with { Content = composer };
    }

    /// <summary>
    /// Seeds the per-user <c>{user}/_Thread/ThreadComposer</c> singleton when a <c>User</c> partition is
    /// created. Returned from <see cref="GetAdditionalNodes"/> so it is persisted directly alongside
    /// the user (no hub round-trip, no access-context plumbing) — the onboarding "initialize the
    /// default state if it doesn't exist" step that keeps the composer read from ever hitting a
    /// routing NotFound.
    /// </summary>
    private sealed class ThreadComposerSeedHandler : INodePostCreationHandler
    {
        public string NodeType => UserNodeType.NodeType; // "User"

        public IObservable<System.Reactive.Unit> Handle(MeshNode createdNode, string? createdBy)
            => System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();

        public IEnumerable<MeshNode> GetAdditionalNodes(MeshNode createdNode)
        {
            var userPath = !string.IsNullOrEmpty(createdNode.Path) ? createdNode.Path : createdNode.Id;
            if (string.IsNullOrEmpty(userPath))
                yield break;

            yield return new MeshNode(ThreadComposerNodeType.NodeType, $"{userPath}/{ThreadNodeType.ThreadPartition}")
            {
                NodeType = ThreadComposerNodeType.NodeType,
                Name = "Chat Input",
                Content = new ThreadComposer(),
            };
        }
    }
}
