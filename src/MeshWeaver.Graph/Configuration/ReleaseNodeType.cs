using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers <c>Release</c> as a first-class NodeType. Release MeshNodes live
/// at <c>{nodeTypePath}/Release/{version}</c> and carry a
/// <see cref="NodeTypeRelease"/> content payload â€” the compiled assembly path,
/// the markdown release notes, the source-input snapshot, and a link to the
/// compile activity that produced them.
///
/// <para>Releases are created by <c>MeshDataSource.InstallCompileWatcher</c>
/// when a compile activity terminates <c>Succeeded</c>; on failure the watcher
/// still writes a Release node so the failure is visible in the release history,
/// but with <c>Status = Failed</c> and <c>AssemblyPath = null</c>.</para>
///
/// <para>See <c>Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md</c>
/// for the design rationale.</para>
/// </summary>
public static class ReleaseNodeType
{
    /// <summary>The node-type identifier string for Release nodes.</summary>
    public const string NodeType = "Release";

    /// <summary>
    /// Registers the Release node type on the mesh builder by adding its MeshNode definition.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder to configure.</param>
    /// <returns>The same builder, to allow fluent chaining.</returns>
    public static TBuilder AddReleaseType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Builds the MeshNode definition for the Release node type, including its content
    /// payload description, excluded contexts, and hub configuration (data source, default
    /// layout areas, and release views).
    /// </summary>
    /// <returns>The Release MeshNode definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Release",
        NodeType = MeshNode.NodeTypePath,   // this MeshNode IS a NodeType definition
        Icon = "/static/NodeTypeIcons/box.svg",
        ExcludeFromContext = new HashSet<string> { "create" }, // no UI create — only the compile watcher writes these
        Content = new NodeTypeDefinition
        {
            Description = "Compiled release of a NodeType. Stores the assembly path, release notes, and a link to the compile activity.",
        },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<NodeTypeRelease>())
            .AddDefaultLayoutAreas()
            .AddReleaseViews()
    };
}
