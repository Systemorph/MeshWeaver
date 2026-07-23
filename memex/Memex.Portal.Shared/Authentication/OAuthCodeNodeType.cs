using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// NodeType definition for <see cref="AuthorizationCode"/> nodes — the pending OAuth
/// authorization codes <see cref="OAuthCodeStore"/> persists at
/// <c>Admin/OAuthCode/{hashPrefix}</c> so a /token exchange on ANY portal replica can
/// redeem a code minted by any other (the in-memory predecessor broke under KEDA
/// scale-out). Short-lived infrastructure rows: single-use (consumed by delete on
/// exchange), 5-minute lifetime, System-identity managed — excluded from search,
/// create menus, and autocomplete.
/// </summary>
public static class OAuthCodeNodeType
{
    /// <summary>The node-type identifier string for OAuth authorization-code nodes.</summary>
    public const string NodeType = "OAuthCode";

    /// <summary>
    /// Registers the OAuthCode node type on the mesh builder: adds the MeshNode type
    /// definition, excludes it from autocomplete, and registers the
    /// <see cref="AuthorizationCode"/> content type so code nodes (de)serialize across
    /// silos and persistence round-trips.
    /// </summary>
    public static TBuilder AddOAuthCodeType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.WithMeshType<AuthorizationCode>();
        return builder;
    }

    /// <summary>Builds the MeshNode type definition for OAuth authorization codes.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "OAuth Authorization Code",
        NodeType = "NodeType",
        Icon = "/static/NodeTypeIcons/key.svg",
        IsSatelliteType = false,
        ExcludeFromContext = System.Collections.Immutable.ImmutableHashSet.Create("search", "create"),
        Content = new NodeTypeDefinition
        {
            Description = "Pending OAuth authorization code (PKCE) awaiting its /token exchange. "
                          + "Stored under Admin/OAuthCode/{hashPrefix}; single-use and short-lived.",
        },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<AuthorizationCode>())
    };
}
