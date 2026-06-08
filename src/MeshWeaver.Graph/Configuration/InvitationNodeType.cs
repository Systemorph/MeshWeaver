using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Invitation nodes in the graph.
/// Invitations authorise specific emails to onboard in invitation-only mode; they are
/// system-managed — excluded from search and create contexts.
///
/// <para>Storage: invitations live in the always-present <b>Admin</b> partition at
/// <c>Admin/Invitation/{slug}</c>. The first-path-segment routing rule in
/// <see cref="GraphConfigurationExtensions"/> already routes any explicit <c>Admin/…</c>
/// path to the Admin partition; the rule registered here additionally routes the
/// path-less onboarding lookup (<c>nodeType:Invitation content.email:X</c>) to the Admin
/// partition so it resolves to a single schema rather than fanning out — the exact pattern
/// <see cref="UserNodeType"/> uses for <c>User → Auth</c>. This deliberately does NOT use the
/// auth-mirror trigger, which only mirrors <c>User/Group/Role/VUser/ApiToken</c> and would
/// silently drop Invitation rows.</para>
/// </summary>
public static class InvitationNodeType
{
    /// <summary>The NodeType value used to identify invitation nodes.</summary>
    public const string NodeType = "Invitation";

    /// <summary>Namespace under which invitation nodes are created (Admin partition).</summary>
    public const string Namespace = "Admin/Invitation";

    /// <summary>The partition (first path segment) invitations live in. Queries must scope to
    /// <c>namespace:Admin</c> to route to the admin schema — the Admin partition is deliberately
    /// excluded from the cross-schema global search (PostgreSqlSchemaInitializer.searchable_schemas).</summary>
    public const string PartitionName = "Admin";

    /// <summary>
    /// Registers the built-in "Invitation" MeshNode on the mesh builder plus the
    /// path-less <c>nodeType:Invitation → Admin</c> query-routing rule.
    /// </summary>
    public static TBuilder AddInvitationType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        // nodeType:Invitation without a path constraint → Admin partition (no fan-out).
        // Queries that already know the path follow the natural first-segment route.
        builder.AddQueryRoutingRule(query =>
            query.ExtractNodeType() == NodeType && string.IsNullOrEmpty(query.Path)
                ? new QueryRoutingHints { Partition = "Admin" }
                : null);
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the Invitation node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Invitation",
        Icon = "/static/NodeTypeIcons/message.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Invitation>())
    };
}
