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
    /// Creates a MeshNode definition for the Thread node type.
    /// This provides HubConfiguration for nodes with nodeType="Thread".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(ThreadNodeType.NodeType)
    {
        Name = "Thread",
        Description = "A conversation thread with AI agents",
        Icon = "Chat",
        HubConfiguration = config => config
            .AddThreadViews()
            .AddMeshDataSource(source => source.WithContentType<MeshThread>())
    };
}
