using System.Collections.Generic;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: MeshWeaver.Hosting.Cosmos.Test.CosmosTestMeshNode]

namespace MeshWeaver.Hosting.Cosmos.Test;

public class CosmosTestMeshNodeAttribute : MeshNodeAttribute
{
    public override IEnumerable<MeshNode> Nodes
        => [CosmosTest];

    public MeshNode CosmosTest => CreateFromHubConfiguration(
        Address,
        nameof(CosmosTest),
        CosmosTestMeshExtensions.ConfigureCosmosTestApplication
    );

    public static readonly Address Address =
        AddressExtensions.CreateAppAddress(nameof(CosmosTest));
}
