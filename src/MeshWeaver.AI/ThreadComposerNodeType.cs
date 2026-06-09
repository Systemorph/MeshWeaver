using System.Collections.Generic;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Per-user <b>ThreadComposer</b> node — the persistent state of a user's chat input box
/// (draft text + the selected harness / agent / model). A per-user <b>singleton</b> stored at
/// <c>{userHome}/_Memex/ThreadComposer</c> as a <b>MAIN</b> node (<see cref="MeshNode.IsSatelliteType"/>
/// = <see langword="false"/> → the partition's <c>mesh_nodes</c> table).
///
/// <para><b>Why under <c>_Memex</c>:</b> <c>_Memex</c> is the user partition's hidden "dotfile"
/// namespace for Memex defaults / global Memex data (see <see cref="MeshNodeVisibility"/> and
/// PostgresSchemaArchitecture.md). The leading underscore hides it from search but — crucially —
/// is NOT a registered satellite suffix, so the write and the path-based read BOTH resolve to
/// <c>mesh_nodes</c> and the selection actually persists. (The dead <c>_ThreadTemplate</c>/
/// <c>nodeType:Thread</c> approach routed the write to the <c>threads</c> satellite table while the
/// path-read hit <c>mesh_nodes</c> → the selection silently never persisted. Never reuse it.)</para>
///
/// <para><b>Why a singleton, seeded at onboarding:</b> the node is materialized for every user when
/// their <c>User</c> partition is created (<see cref="ThreadComposerSeedHandler"/>, a
/// <see cref="INodePostCreationHandler"/>), so the chat composer's read always RESOLVES instead of
/// generating a routing NotFound that the GUI re-issues on a loop — the 2026-06-08 event-storm
/// class. The content is the dedicated <see cref="ThreadComposer"/> record (message text + the
/// harness/agent/model comboboxes + attachments) — exactly the fields the out-of-thread composer
/// owns, not the full conversation <see cref="Thread"/> shape.</para>
/// </summary>
public static class ThreadComposerNodeType
{
    /// <summary>NodeType discriminator AND the singleton instance id for the per-user chat-input state node.</summary>
    public const string NodeType = "ThreadComposer";

    /// <summary>
    /// The user partition's hidden Memex-defaults namespace segment (a dotfile): the singleton lives at
    /// <c>{userHome}/_Memex/ThreadComposer</c>. Hidden from search by <see cref="MeshNodeVisibility"/>, but
    /// NOT a satellite suffix, so write + read both hit <c>mesh_nodes</c>.
    /// </summary>
    public const string MemexDefaultsNamespace = "_Memex";

    /// <summary>Registers the ThreadComposer type node and the per-user singleton seed handler.</summary>
    public static TBuilder AddThreadComposerType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureServices(services =>
        {
            // Seed {user}/_Memex/ThreadComposer when a User partition is onboarded so the
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
        // Main node, NOT a satellite → mesh_nodes, so the path-based read and the write
        // resolve to the same table and the selection actually persists. Instances live
        // under the hidden {user}/_Memex namespace (a dotfile path), so they're already
        // excluded from search; the type node itself is hidden from the create menu and
        // search so users never hand-create a ThreadComposer.
        IsSatelliteType = false,
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
    /// Seeds the per-user <c>{user}/_Memex/ThreadComposer</c> singleton when a <c>User</c> partition is
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
