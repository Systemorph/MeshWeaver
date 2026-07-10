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

    // NOTE: the old AddPluginCatalog(sourceRepoPath, …) overload — which seeded a browsable
    // "Plugins" Space + a PluginCatalog node — was removed. The catalog is now a platform-admin
    // Settings tab (PluginCatalogSettingsTab) reading a REMOTE registry over HTTP, and a registry
    // instance exposes its source via /api/plugins. Install records still live in the "Plugins"
    // partition (as Package nodes), but there is no browsable Space root, so no user can navigate
    // into it and hit "Access denied on 'Plugins'".

    /// <summary>Registers the plugin-catalog content types under their short names.</summary>
    /// <param name="typeRegistry">The type registry to populate.</param>
    /// <returns>The same type registry, for chaining.</returns>
    public static ITypeRegistry AddPluginCatalogTypes(this ITypeRegistry typeRegistry)
        => typeRegistry
            .WithType(typeof(PackageManifest), nameof(PackageManifest))
            .WithType(typeof(PluginCatalogContent), nameof(PluginCatalogContent))
            .WithType(typeof(PluginManifest), nameof(PluginManifest));

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
