using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Social;

/// <summary>
/// NodeType definition for <see cref="PlatformCredential"/>. Instances live under
/// <c>{profilePath}/_ApiCredentials/{platform}</c> and are read/written exclusively
/// by this module: the LinkedIn/X connect callbacks write them, the publisher reads them.
///
/// <para>🚨 It lives HERE, with the module, and not in the host. A credential type is not a
/// platform concept — no platform feature stores one (GitHub Sync keeps its own
/// <c>GitHubCredentialService</c>) — so registering it host-side made the portal compile against
/// this module for one type, which was the LAST thing keeping the module in the platform's build
/// graph. The module registering its own types is what a module IS.</para>
///
/// <para>Nothing about stored credentials changes: the node type is still <c>ApiCredential</c> and
/// the content discriminator is still <c>PlatformCredential</c> — the same strings, registered by a
/// different assembly, so existing <c>{profile}/_ApiCredentials/{platform}</c> nodes keep
/// deserializing and activating. Access control stays where it is: a satellite access rule in the
/// host's security config, keyed on the type NAME.</para>
/// </summary>
public static class ApiCredentialNodeType
{
    public const string NodeType = "ApiCredential";

    public static TBuilder AddApiCredentialType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.WithMeshType<PlatformCredential>();
        return builder;
    }

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "API Credential",
        NodeType = "NodeType",
        Icon = "/static/NodeTypeIcons/key.svg",
        IsSatelliteType = true,
        Content = new NodeTypeDefinition
        {
            Description = "OAuth credentials for a platform (LinkedIn, X). Stored under {profile}/_ApiCredentials/.",
        },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<PlatformCredential>())
    };
}
