using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The platform-side DECLARATION of the legacy <c>TrackedChange</c> satellite node type.
///
/// <para>🚨 Nothing writes these any more — tracked changes are a view model projected from the
/// version history by the collaboration module. The declaration survives here for the same reason
/// <see cref="CommentNodeType"/>'s does: <c>_Tracking</c> rows written by older builds must stay
/// installable and readable on a mesh that does not carry the module.</para>
/// </summary>
public static class TrackedChangeNodeType
{
    /// <summary>The NodeType value identifying tracked change nodes.</summary>
    public const string NodeType = "TrackedChange";

    /// <summary>
    /// Registers the built-in <c>TrackedChange</c> MeshNode on the mesh builder.
    /// </summary>
    /// <typeparam name="TBuilder">The concrete mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder to register on.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder AddTrackedChangeType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates the MeshNode definition for the <c>TrackedChange</c> node type.
    /// </summary>
    /// <returns>The node definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "TrackedChange",
        Icon = "/static/NodeTypeIcons/document.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<MeshWeaver.Mesh.TrackedChange>())
            .ApplyNodeHubContributions(NodeType)
    };
}
