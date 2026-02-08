using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Graph-specific extensions for ThreadMessage node types.
/// Creates MeshNode definitions with HubConfiguration.
/// </summary>
public static class ThreadMessageNodeConfiguration
{
    /// <summary>
    /// Creates a MeshNode definition for the ThreadMessage node type.
    /// This provides HubConfiguration for nodes with nodeType="ThreadMessage".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(ThreadMessageNodeType.NodeType)
    {
        Name = "Thread Message",
        Description = "A single message in a conversation thread",
        Icon = "Chat",
        HubConfiguration = config => config
            .AddThreadMessageViews()
            .AddMeshDataSource(source => source.WithContentType<ThreadMessage>())
    };
}
