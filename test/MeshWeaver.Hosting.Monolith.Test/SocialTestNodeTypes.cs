using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Test-fixture NodeType registrations for the LinkedIn publish integration tests.
/// </summary>
internal static class SocialTestNodeTypes
{
    /// <summary>
    /// Registers <c>Systemorph/Post</c> as a <b>generic</b> NodeType — a data source with no CLR content
    /// type, so instances round-trip their <c>Dictionary</c> content as a <see cref="System.Text.Json.JsonElement"/>,
    /// exactly as the production social-media Post nodes do (the layout areas read them as JsonElement).
    /// </summary>
    public static MeshNode PostNodeType => new("Post", "Systemorph")
    {
        Name = "Social Media Post",
        NodeType = "NodeType",
        Content = new NodeTypeDefinition { Description = "Social media post (test fixture)." },
        HubConfiguration = config => config.AddMeshDataSource(),
    };
}
