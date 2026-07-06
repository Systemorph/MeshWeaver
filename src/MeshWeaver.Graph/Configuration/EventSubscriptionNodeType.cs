using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers the built-in "EventSubscription" MeshNode — the durable "when THIS trigger fires, run THAT
/// continuation" record (see <see cref="EventSubscription"/>). System-managed, excluded from search and
/// create contexts.
///
/// <para>Storage mirrors <see cref="ScheduledActionNodeType"/> (which it supersedes): the nodes live in
/// the always-present <b>Admin</b> partition at <c>Admin/EventSubscription/{id}</c>. The PG router routes
/// by the path's first segment, so the runner enumerates them path-scoped
/// (<c>path:Admin/EventSubscription scope:children</c>); the routing hint lets a path-less
/// <c>nodeType:EventSubscription</c> query resolve too.</para>
/// </summary>
public static class EventSubscriptionNodeType
{
    /// <summary>The NodeType value identifying event-subscription nodes.</summary>
    public const string NodeType = "EventSubscription";

    /// <summary>Namespace under which event-subscription nodes are created (Admin partition).</summary>
    public const string Namespace = "Admin/EventSubscription";

    /// <summary>The partition (first path segment) event subscriptions live in.</summary>
    public const string PartitionName = "Admin";

    /// <summary>The path of an event-subscription node: <c>Admin/EventSubscription/{id}</c>.</summary>
    public static string Path(string id) => $"{Namespace}/{id}";

    /// <summary>
    /// Registers the built-in "EventSubscription" MeshNode plus the path-less
    /// <c>nodeType:EventSubscription → Admin</c> query-routing hint.
    /// </summary>
    public static TBuilder AddEventSubscriptionType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.AddQueryRoutingRule(query =>
            query.ExtractNodeType() == NodeType && string.IsNullOrEmpty(query.Path)
                ? new QueryRoutingHints { Partition = "Admin" }
                : null);
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the EventSubscription node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Event Subscription",
        Icon = "/static/NodeTypeIcons/clock.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<EventSubscription>())
    };
}
