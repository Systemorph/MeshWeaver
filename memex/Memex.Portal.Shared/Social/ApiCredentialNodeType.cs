using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Social;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// NodeType definition for <see cref="PlatformCredential"/>. Instances live under
/// <c>{profilePath}/_ApiCredentials/{platform}</c> and are read/written exclusively
/// by the Social subsystem + the LinkedIn/X connect endpoints. Access control:
/// readable/writable only by Admins and the profile owner — wired via a satellite
/// access rule in the hosting app (Memex security config). This file only registers
/// the type shape.
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
        AssemblyLocation = typeof(ApiCredentialNodeType).Assembly.Location,
        IsSatelliteType = true,
        Content = new NodeTypeDefinition
        {
            Description = "OAuth credentials for a platform (LinkedIn, X). Stored under {profile}/_ApiCredentials/.",
            ShowChildrenInDetails = false,
        },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<PlatformCredential>())
    };
}
