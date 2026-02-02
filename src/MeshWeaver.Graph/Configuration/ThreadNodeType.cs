using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Thread node types in the graph.
/// Thread nodes store AI conversation history with hierarchical support
/// for agent delegations stored as child nodes.
/// </summary>
public static class ThreadNodeType
{
    /// <summary>
    /// The NodeType value used to identify thread nodes.
    /// </summary>
    public const string NodeType = "Thread";

    /// <summary>
    /// Gets the user's thread catalog path.
    /// </summary>
    /// <param name="userId">The user's unique identifier.</param>
    /// <returns>Path to the user's threads namespace.</returns>
    public static string GetUserThreadsPath(string userId) => $"User/{userId}/Threads";

    /// <summary>
    /// Creates a MeshNode definition for the Thread node type.
    /// This provides HubConfiguration for nodes with nodeType="Thread".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Thread",
        Description = "A conversation thread with AI agents",
        Icon = "Chat",
        HubConfiguration = config => config
            .AddThreadViews()
            .AddMeshDataSource(source => source.WithContentType<ThreadNodeContent>())
    };
}
