using MeshWeaver.Mesh;
using MeshWeaver.Northwind.ViewModel;
[assembly: NorthwindApplication]

namespace MeshWeaver.Northwind.ViewModel;


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
    public static readonly MeshNode Northwind = new(
        ApplicationAddress.TypeName,
        nameof(Northwind),
        nameof(Northwind)
    )
    {
        HubConfiguration = NorthwindApplicationExtensions.ConfigureApplication,
    };
}
