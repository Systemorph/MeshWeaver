using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers the built-in "ScheduledAction" MeshNode. Scheduled actions are deferred,
/// event-triggered effects (see <see cref="ScheduledAction"/>) — system-managed, excluded from
/// search and create contexts.
///
/// <para>Storage mirrors <see cref="InvitationNodeType"/>: the nodes live in the always-present
/// <b>Admin</b> partition at <c>Admin/ScheduledAction/{id}</c>. Queries must be <b>path-scoped</b>
/// (<c>path:Admin/ScheduledAction scope:children</c>) to route to the admin schema — a
/// <c>namespace:Admin</c>-only query fans out cross-schema and deliberately EXCLUDES the admin
/// schema. The runner enumerates them this way on startup to reconcile outstanding actions.</para>
/// </summary>
public static class ScheduledActionNodeType
{
    /// <summary>The NodeType value identifying scheduled-action nodes.</summary>
    public const string NodeType = "ScheduledAction";

    /// <summary>Namespace under which scheduled-action nodes are created (Admin partition).</summary>
    public const string Namespace = "Admin/ScheduledAction";

    /// <summary>The partition (first path segment) scheduled actions live in.</summary>
    public const string PartitionName = "Admin";

    /// <summary>The path of a scheduled-action node: <c>Admin/ScheduledAction/{id}</c>.</summary>
    public static string Path(string id) => $"{Namespace}/{id}";

    /// <summary>
    /// Registers the built-in "ScheduledAction" MeshNode plus the path-less
    /// <c>nodeType:ScheduledAction → Admin</c> query-routing hint.
    /// </summary>
    public static TBuilder AddScheduledActionType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.AddQueryRoutingRule(query =>
            query.ExtractNodeType() == NodeType && string.IsNullOrEmpty(query.Path)
                ? new QueryRoutingHints { Partition = "Admin" }
                : null);
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the ScheduledAction node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Scheduled Action",
        Icon = "/static/NodeTypeIcons/clock.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ScheduledAction>())
    };
}
