using System.Collections.Generic;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: MeshWeaver.Hosting.Orleans.Test.OrleansTestMeshNode]

namespace MeshWeaver.Hosting.Orleans.Test;

public class OrleansTestMeshNodeAttribute : MeshNodeProviderAttribute
{
    /// <summary>
    /// Mesh catalog entry.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes
        => [OrleansTest];

    /// <summary>
    /// Main definition of the mesh node.
    /// </summary>
    public MeshNode OrleansTest => CreateFromHubConfiguration(
        Address,
        nameof(OrleansTest),
        OrleansTestMeshExtensions.ConfigureOrleansTestApplication
    );

    public static readonly Address Address =
        AddressExtensions.CreateAppAddress(nameof(OrleansTest));
}
