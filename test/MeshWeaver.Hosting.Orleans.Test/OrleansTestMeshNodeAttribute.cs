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
    public static readonly MeshNode OrleansTest = new(
        ApplicationAddress.TypeName,
        nameof(OrleansTest),
        nameof(OrleansTest)
    )
    {
        HubConfiguration = OrleansTestMeshExtensions.ConfigureOrleansTestApplication,
    };

}
