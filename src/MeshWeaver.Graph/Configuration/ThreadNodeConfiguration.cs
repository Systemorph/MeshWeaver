using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Graph-specific extensions for Thread node types.
/// Creates MeshNode definitions with HubConfiguration.
/// </summary>
public static class ThreadNodeConfiguration
{
    /// <summary>
    /// Registers the built-in "Thread" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddThreadType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(ThreadNodeType.NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Thread node type.
    /// This provides HubConfiguration for nodes with nodeType="Thread".
    /// Thread messages are stored as child MeshNodes with nodeType="ThreadMessage".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(ThreadNodeType.NodeType)
    {
        Name = "Thread",
        Icon = "/static/NodeTypeIcons/chat.svg",
        AssemblyLocation = typeof(ThreadNodeConfiguration).Assembly.Location,
        HubConfiguration = config => config
            .AddThreadViews()
            .AddMeshDataSource(source => source
                .WithContentType<MeshThread>()
                .WithType<ThreadMessage>(ThreadMessageNodeType.NodeType))
    };
}
