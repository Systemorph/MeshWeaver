using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Notification nodes in the graph.
/// Notification nodes are system-generated — excluded from search and create contexts.
/// </summary>
public static class NotificationNodeType
{
    /// <summary>
    /// The NodeType value used to identify notification nodes.
    /// </summary>
    public const string NodeType = "Notification";

    /// <summary>
    /// Registers the built-in "Notification" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddNotificationType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Notification node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Notification",
        Icon = "/static/NodeTypeIcons/bell.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(NotificationNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddNotificationViews()
            .AddMeshDataSource(source => source
                .WithContentType<Notification>())
    };
}
