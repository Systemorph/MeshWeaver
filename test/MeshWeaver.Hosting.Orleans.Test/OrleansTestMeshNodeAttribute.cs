using System.Collections.Generic;
using MeshWeaver.Mesh;

[assembly: MeshWeaver.Hosting.Orleans.Test.OrleansTestMeshNode]

namespace MeshWeaver.Hosting.Orleans.Test;

public class OrleansTestMeshNodeAttribute : MeshNodeAttribute
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

    public static readonly ApplicationAddress Address = 
        new ApplicationAddress(nameof(OrleansTest));
}
