using MeshWeaver.Graph;

namespace MeshWeaver.AI;

/// <summary>
/// Graph-specific extensions for ThreadMessage node types.
/// Creates MeshNode definitions with HubConfiguration.
/// </summary>
public static class ThreadMessageNodeConfiguration
{
    /// <summary>
    /// Registers the built-in "ThreadMessage" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddThreadMessageType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(ThreadMessageNodeType.NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the ThreadMessage node type.
    /// This provides HubConfiguration for nodes with nodeType="ThreadMessage".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(ThreadMessageNodeType.NodeType)
    {
        Name = "Thread Message",
        Icon = "/static/NodeTypeIcons/message.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(ThreadMessageNodeConfiguration).Assembly.Location,
        HubConfiguration = config => config
            .AddThreadMessageViews()
            .AddMeshDataSource(source => source.WithContentType<ThreadMessage>())
    };
}
