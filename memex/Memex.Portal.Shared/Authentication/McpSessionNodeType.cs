using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// NodeType definition for <see cref="McpSessionEntry"/> nodes — the stateful MCP session
/// records <see cref="McpSessionStore"/> persists at <c>Admin/McpSession/{hashPrefix}</c> so a
/// session established on one portal replica can be re-hydrated on any other (the MCP client
/// carries no affinity cookie, so a follow-up request can land on a replica that never served
/// the session's <c>initialize</c>). Infrastructure rows: System-identity managed, owner-scoped,
/// excluded from search, create menus, and autocomplete — the same treatment as
/// <see cref="OAuthCodeNodeType"/>.
/// </summary>
public static class McpSessionNodeType
{
    /// <summary>The node-type identifier string for MCP session nodes.</summary>
    public const string NodeType = "McpSession";

    /// <summary>
    /// Registers the McpSession node type on the mesh builder: adds the MeshNode type
    /// definition, excludes it from autocomplete, and registers the
    /// <see cref="McpSessionEntry"/> content type so session nodes (de)serialize across silos
    /// and persistence round-trips. Wired in the portal's <c>ConfigureMemexMesh</c> next to
    /// <c>AddOAuthCodeType()</c>.
    /// </summary>
    public static TBuilder AddMcpSessionType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.WithMeshType<McpSessionEntry>();
        return builder;
    }

    /// <summary>Builds the MeshNode type definition for MCP session nodes.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "MCP Session",
        NodeType = "NodeType",
        Icon = "/static/NodeTypeIcons/key.svg",
        IsSatelliteType = false,
        ExcludeFromContext = System.Collections.Immutable.ImmutableHashSet.Create("search", "create"),
        Content = new NodeTypeDefinition
        {
            Description = "A stateful MCP Streamable-HTTP session's initialize params, persisted so any "
                          + "portal replica can re-hydrate the session. Stored under Admin/McpSession/{hashPrefix}.",
        },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<McpSessionEntry>())
    };
}
