using MeshWeaver.Graph;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI;

/// <summary>
/// Per-user <b>ChatInput</b> node — the persistent state of a user's chat input box
/// (draft text + the selected harness / agent / model). Stored at
/// <c>{userHome}/ChatInput</c> as a <b>MAIN</b> node (<see cref="MeshNode.IsSatelliteType"/>
/// = <see langword="false"/> → the partition's <c>mesh_nodes</c> table).
///
/// <para>Why a dedicated type rather than the old <c>nodeType:Thread</c> template: a
/// <c>Thread</c> node routes the WRITE to the per-partition <c>threads</c> SATELLITE table,
/// but <c>GetMeshNodeStream</c> resolves the READ by path to <c>mesh_nodes</c> — so the
/// template was written to one table and read from another and <b>never persisted</b> (the
/// chosen harness/agent/model + draft were lost on every reload). A non-satellite type keeps
/// write + read on the same <c>mesh_nodes</c> table. The content reuses the
/// <see cref="Thread"/> shape (it already carries the draft + selection fields).</para>
/// </summary>
public static class ChatInputNodeType
{
    /// <summary>NodeType discriminator for the per-user chat-input state node.</summary>
    public const string NodeType = "ChatInput";

    /// <summary>Registers the ChatInput type node (its hub serves the <see cref="Thread"/> content).</summary>
    public static TBuilder AddChatInputType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>The type-definition node for nodeType="ChatInput".</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Chat Input",
        Icon = "/static/NodeTypeIcons/message.svg",
        // Main node, NOT a satellite → mesh_nodes, so the path-based read and the write
        // resolve to the same table and the selection actually persists.
        IsSatelliteType = false,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Thread>())
    };
}
