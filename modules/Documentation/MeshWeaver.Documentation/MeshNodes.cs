using MeshWeaver.Documentation;
using MeshWeaver.Mesh;
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
    public MeshNode Documentation => CreateFromHubConfiguration(
        new ApplicationAddress(nameof(Documentation)),
        nameof(Documentation),
        DocumentationApplicationExtensions.ConfigureDocumentation
    );
}
