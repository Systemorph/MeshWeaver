using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Entry point for the MeshWeaver plugin catalog — the mesh's git-based "app store". Registers the
/// <c>Package</c> node type (the install-record shape) and the <see cref="PackageManifest"/> content
/// type so install records round-trip across hubs. The catalog browse/install UI + a source-configured
/// catalog node build on top of this. Git-based end to end; NO NuGet.
/// </summary>
public static class PluginCatalogConfigurationExtensions
{
    /// <summary>The NodeType of a catalog node (source-configured browse/install view).</summary>
    public const string CatalogNodeType = "PluginCatalog";

    /// <summary>
    /// Registers the plugin catalog on the mesh builder: the <c>Package</c> install-record node type
    /// and the <c>PluginCatalog</c> browse node type, plus their content types on the mesh + every
    /// per-node hub so they round-trip across hubs.
    /// </summary>
    /// <typeparam name="TBuilder">The concrete mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder AddPluginCatalog<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => (TBuilder)builder
            .AddMeshNodes(CreatePackageNodeType())
            .AddMeshNodes(CreateCatalogNodeType())
            .ConfigureHub(config =>
            {
                config.TypeRegistry.AddPluginCatalogTypes();
                return config;
            })
            .ConfigureDefaultNodeHub(config =>
            {
                config.TypeRegistry.AddPluginCatalogTypes();
                return config;
            });

    /// <summary>
    /// Registers the plugin catalog AND seeds a ready-to-use catalog: a <c>Plugins</c> Space (a valid
    /// partition root — a custom type can't be one) whose <c>Plugins/catalog</c> child is a
    /// <c>PluginCatalog</c> node pointed at <paramref name="sourceRepoPath"/>. Install records land in
    /// the same <c>Plugins</c> partition (as siblings), so the catalog is the space's home. When
    /// <paramref name="sourceRepoPath"/> is empty the catalog renders a "configure me" prompt.
    /// </summary>
    /// <typeparam name="TBuilder">The concrete mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <param name="sourceRepoPath">Local path to the source git repo (the plugins checkout).</param>
    /// <param name="sourceSubdir">Subdirectory holding the package folders (default <c>"catalog"</c>).</param>
    /// <param name="sourceRef">Git ref to browse/install from (default <c>"HEAD"</c>).</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder AddPluginCatalog<TBuilder>(
        this TBuilder builder, string sourceRepoPath, string sourceSubdir = "catalog", string sourceRef = "HEAD")
        where TBuilder : MeshBuilder
        => (TBuilder)builder
            .AddPluginCatalog()
            .AddMeshNodes(
                new MeshNode(PackageInstaller.InstalledPartition)
                {
                    Name = "Plugins",
                    NodeType = "Space",
                    State = MeshNodeState.Active,
                    Content = new MarkdownContent
                    {
                        Content = "# Plugins\n\nThe plugin catalog and installed packages.",
                    },
                },
                new MeshNode("catalog", PackageInstaller.InstalledPartition)
                {
                    Name = "Plugin Catalog",
                    NodeType = CatalogNodeType,
                    State = MeshNodeState.Active,
                    Content = new PluginCatalogContent
                    {
                        SourceRepoPath = sourceRepoPath,
                        SourceSubdir = sourceSubdir,
                        SourceRef = sourceRef,
                    },
                });

    /// <summary>Registers the plugin-catalog content types under their short names.</summary>
    /// <param name="typeRegistry">The type registry to populate.</param>
    /// <returns>The same type registry, for chaining.</returns>
    public static ITypeRegistry AddPluginCatalogTypes(this ITypeRegistry typeRegistry)
        => typeRegistry
            .WithType(typeof(PackageManifest), nameof(PackageManifest))
            .WithType(typeof(PluginCatalogContent), nameof(PluginCatalogContent));

    private static MeshNode CreatePackageNodeType() => new(PackageInstaller.PackageNodeType)
    {
        Name = "Package",
        Icon = "/static/NodeTypeIcons/box.svg",
        HubConfiguration = config => config
            .AddDefaultLayoutAreas()
            .AddMeshDataSource(s => s.WithContentType<PackageManifest>()),
    };

    private static MeshNode CreateCatalogNodeType() => new(CatalogNodeType)
    {
        Name = "Plugin Catalog",
        Icon = "/static/NodeTypeIcons/box.svg",
        HubConfiguration = config => config.AddPluginCatalogViews(),
    };
}
