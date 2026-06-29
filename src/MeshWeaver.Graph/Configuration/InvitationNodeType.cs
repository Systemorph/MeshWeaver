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
/// <c>Admin/Invitation/{slug}</c>. The PG query router routes by the path's first segment, so
/// callers must query them <b>path-scoped</b> (<c>path:Admin/Invitation scope:children</c>) to
/// hit the admin schema — a <c>namespace:Admin</c>-only query fans out cross-schema, which
/// deliberately EXCLUDES the admin schema. This deliberately does NOT use the auth-mirror
/// trigger, which only mirrors <c>User/Group/Role/VUser/ApiToken</c> and would silently drop
/// Invitation rows.</para>
/// </summary>
public static class InvitationNodeType
{
    /// <summary>The NodeType value used to identify invitation nodes.</summary>
    public const string NodeType = "Invitation";

    /// <summary>Namespace under which invitation nodes are created (Admin partition).</summary>
    public const string Namespace = "Admin/Invitation";

    /// <summary>The partition (first path segment) invitations live in. Queries must be
    /// <b>path-scoped</b> (<c>path:Admin/Invitation scope:children</c>) to route to the admin
    /// schema: the PG router routes by the path's FIRST SEGMENT
    /// (<c>PostgreSqlPartitionedMeshQuery.FirstSegment</c>), so a <c>namespace:Admin</c>-only
    /// query has no path, fans out cross-schema, and the admin schema is deliberately EXCLUDED
    /// from that fan-out (PostgreSqlSchemaInitializer.searchable_schemas) — it would find nothing.
    /// (<c>namespace:Admin</c> is also exact-match and would miss the <c>Admin/Invitation</c>
    /// namespace regardless.)</summary>
    public const string PartitionName = "Admin";

    /// <summary>
    /// Registers the built-in "Invitation" MeshNode on the mesh builder plus the
    /// path-less <c>nodeType:Invitation → Admin</c> query-routing rule.
    /// </summary>
    public static TBuilder AddInvitationType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        // Intent: a path-less nodeType:Invitation query → Admin partition (no fan-out).
        // NOTE: the PostgreSQL query router (PostgreSqlPartitionedMeshQuery) routes purely by
        // the path's first segment and does NOT consume these QueryRoutingHints yet, so this
        // rule is currently inert — invitation queries MUST therefore be path-scoped
        // (path:Admin/Invitation), which is what InvitationService / InvitationEmailSender /
        // InvitationsSettingsTab now do. Kept so the hint is in place once the router honours it.
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
