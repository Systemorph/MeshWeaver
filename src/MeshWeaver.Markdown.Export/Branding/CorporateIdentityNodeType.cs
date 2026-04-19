using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown.Export.Branding;

/// <summary>
/// Registers the <c>CorporateIdentity</c> mesh node type so brand nodes surface
/// in the brand picker and deserialize with the correct content type.
/// </summary>
public static class CorporateIdentityNodeType
{
    /// <summary>The NodeType value for corporate-identity nodes.</summary>
    public const string NodeType = "CorporateIdentity";

    /// <summary>
    /// Creates the MeshNode descriptor for the CorporateIdentity node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Corporate Identity",
        Icon = "/static/NodeTypeIcons/organization.svg",
        AssemblyLocation = typeof(CorporateIdentityNodeType).Assembly.Location,
        HubConfiguration = config => config
            .WithTypes(typeof(CorporateIdentity))
    };
}
