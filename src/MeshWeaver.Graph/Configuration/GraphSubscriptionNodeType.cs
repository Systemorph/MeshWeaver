using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configuration for <b>GraphSubscription</b> nodes — the persisted id/expiry of the inbound-mail Graph
/// change-notification subscription, so a portal restart renews/reuses it instead of creating a duplicate.
/// System-managed (excluded from search/create); written by the subscription service.
/// </summary>
public static class GraphSubscriptionNodeType
{
    /// <summary>The NodeType value used to identify Graph-subscription state nodes.</summary>
    public const string NodeType = "GraphSubscription";

    /// <summary>Stable path for the single inbox subscription record.</summary>
    public const string InboxPath = "Admin/_GraphSubscription/inbox";

    /// <summary>Registers the built-in "GraphSubscription" MeshNode on the mesh builder.</summary>
    public static TBuilder AddGraphSubscriptionType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the GraphSubscription node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Graph Subscription",
        Icon = "/static/NodeTypeIcons/mail.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<GraphSubscriptionState>())
    };
}
