using MeshWeaver.Mesh;
using MeshWeaver.Documentation;
[assembly: DocumentationApplication]

namespace MeshWeaver.Documentation;


/// <summary>
/// This is the configuration of the Northwind application mesh node.
/// </summary>
public class DocumentationApplicationAttribute : MeshNodeAttribute
{
    /// <summary>
    /// Mesh catalog entry.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes
        => [Documentation];
    /// <summary>
    /// Main definition of the mesh node.
    /// </summary>
    public static readonly MeshNode Documentation = new(
        ApplicationAddress.TypeName,
        nameof(Documentation),
        nameof(Documentation)
    )
    {
        HubConfiguration = DocumentationViewModels.AddDocumentation
    };
}
