using MeshWeaver.Mesh;
using MeshWeaver.Northwind.Application;

[assembly: NorthwindApplication]

namespace MeshWeaver.Northwind.Application;


/// <summary>
/// This is the configuration of the Northwind application mesh node.
/// </summary>
public class NorthwindApplicationAttribute : MeshNodeAttribute
{
    /// <summary>
    /// Mesh catalog entry.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes
        => [Northwind];
    /// <summary>
    /// Main definition of the mesh node.
    /// </summary>
    public MeshNode Northwind => CreateFromHubConfiguration(
        new ApplicationAddress(nameof(Northwind)),
        nameof(Northwind),
        NorthwindApplicationExtensions.ConfigureNorthwindApplication
    );
}
