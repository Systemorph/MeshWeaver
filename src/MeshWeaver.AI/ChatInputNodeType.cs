using System.Collections.Generic;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// Per-user <b>ChatInput</b> node — the persistent state of a user's chat input box
/// (draft text + the selected harness / agent / model). A per-user <b>singleton</b> stored at
/// <c>{userHome}/_Memex/ChatInput</c> as a <b>MAIN</b> node (<see cref="MeshNode.IsSatelliteType"/>
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
/// their <c>User</c> partition is created (<see cref="ChatInputSeedHandler"/>, a
/// <see cref="INodePostCreationHandler"/>), so the chat composer's read always RESOLVES instead of
/// generating a routing NotFound that the GUI re-issues on a loop — the 2026-06-08 event-storm
/// class. The content is the dedicated <see cref="ChatInput"/> record (message text + the
/// harness/agent/model comboboxes + attachments) — exactly the fields the out-of-thread composer
/// owns, not the full conversation <see cref="Thread"/> shape.</para>
/// </summary>
public static class ChatInputNodeType
{
    /// <summary>NodeType discriminator AND the singleton instance id for the per-user chat-input state node.</summary>
    public const string NodeType = "ChatInput";

    /// <summary>
    /// The user partition's hidden Memex-defaults namespace segment (a dotfile): the singleton lives at
    /// <c>{userHome}/_Memex/ChatInput</c>. Hidden from search by <see cref="MeshNodeVisibility"/>, but
    /// NOT a satellite suffix, so write + read both hit <c>mesh_nodes</c>.
    /// </summary>
    public const string MemexDefaultsNamespace = "_Memex";

    /// <summary>Registers the ChatInput type node and the per-user singleton seed handler.</summary>
    public static TBuilder AddChatInputType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureServices(services =>
        {
            // Seed {user}/_Memex/ChatInput when a User partition is onboarded so the
            // composer's read always resolves (no read-before-create NotFound storm).
            services.AddSingleton<INodePostCreationHandler>(_ => new ChatInputSeedHandler());
            return services;
        });
        return builder;
    }

    /// <summary>The type-definition node for nodeType="ChatInput".</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Chat Input",
        Icon = "/static/NodeTypeIcons/message.svg",
        // Main node, NOT a satellite → mesh_nodes, so the path-based read and the write
        // resolve to the same table and the selection actually persists. Instances live
        // under the hidden {user}/_Memex namespace (a dotfile path), so they're already
        // excluded from search; the type node itself is hidden from the create menu and
        // search so users never hand-create a ChatInput.
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ChatInput>())
            // The composer is the node's default ("") layout area — see ChatInputView.
            .AddChatInputView()
    };

    /// <summary>
    /// The per-user ChatInput singleton path under a user's partition:
    /// <c>{userHome}/_Memex/ChatInput</c>.
    /// </summary>
    public static string PathFor(string userHome) => $"{userHome}/{MemexDefaultsNamespace}/{NodeType}";

    /// <summary>
    /// Seeds the per-user <c>{user}/_Memex/ChatInput</c> singleton when a <c>User</c> partition is
    /// created. Returned from <see cref="GetAdditionalNodes"/> so it is persisted directly alongside
    /// the user (no hub round-trip, no access-context plumbing) — the onboarding "initialize the
    /// default state if it doesn't exist" step that keeps the composer read from ever hitting a
    /// routing NotFound.
    /// </summary>
    private sealed class ChatInputSeedHandler : INodePostCreationHandler
    {
        public string NodeType => UserNodeType.NodeType; // "User"

        public IObservable<System.Reactive.Unit> Handle(MeshNode createdNode, string? createdBy)
            => System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();

        public IEnumerable<MeshNode> GetAdditionalNodes(MeshNode createdNode)
        {
            var userPath = !string.IsNullOrEmpty(createdNode.Path) ? createdNode.Path : createdNode.Id;
            if (string.IsNullOrEmpty(userPath))
                yield break;

            yield return new MeshNode(ChatInputNodeType.NodeType, $"{userPath}/{MemexDefaultsNamespace}")
            {
                NodeType = ChatInputNodeType.NodeType,
                Name = "Chat Input",
                Content = new ChatInput(),
            };
        }
    }
}
