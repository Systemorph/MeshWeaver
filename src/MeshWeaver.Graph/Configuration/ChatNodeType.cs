using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Chat node types in the graph.
/// Chat nodes store AI conversation history with hierarchical support
/// for agent delegations stored as child nodes.
/// </summary>
public static class ChatNodeType
{
    /// <summary>
    /// The NodeType value used to identify chat nodes.
    /// </summary>
    public const string NodeType = "Chat";

    /// <summary>
    /// Gets the user's chat catalog path.
    /// </summary>
    /// <param name="userId">The user's unique identifier.</param>
    /// <returns>Path to the user's chats namespace.</returns>
    public static string GetUserChatsPath(string userId) => $"User/{userId}/Chats";

    /// <summary>
    /// Creates a MeshNode definition for the Chat node type.
    /// This provides HubConfiguration for nodes with nodeType="Chat".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Chat",
        Description = "A chat conversation with AI agents",
        Icon = "Chat",
        HubConfiguration = config => config
            .AddChatViews()
            .AddMeshDataSource(source => source.WithContentType<ChatNodeContent>())
    };
}
