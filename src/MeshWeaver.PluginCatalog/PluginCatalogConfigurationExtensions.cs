using MeshWeaver.Domain;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

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
            .AddMeshNodes(CreateInstalledPartitionPolicy())
            // The build-completion subscriber. A mesh-scoped SINGLETON, so its subscriptions live
            // and die with the mesh rather than surviving disposal into the next test
            // (Doc/Architecture/NoStaticState). The IHostedService registration is what STARTS it —
            // the host only starts services registered under the interface, so the bare singleton
            // alone would leave the build-node subscription never opened (Copilot catch). Forwarded
            // to the same instance so start/stop and the mesh singleton are one object.
            .ConfigureServices(services => services
                .AddSingleton<PluginUpdateWatcher>()
                .AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                    sp => sp.GetRequiredService<PluginUpdateWatcher>()))
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

    // Read-only, world-readable policy for the install-records partition — the same shape every
    // other built-in catalog ships (BuiltInAgentProvider / BuiltInSkillProvider / the model
    // catalog). The records are written exclusively under ImpersonateAsSystem (PackageInstaller),
    // so no creator grant is ever minted, and a platform admin's Admin/_Access grant is scoped to
    // the Admin partition — without this policy NO real signed-in principal holds Read on
    // "Plugins", and the settings tab's installed-state query (CatalogLayoutAreas.ObserveInstalled,
    // `path:Plugins scope:children`) is denied for the very admin the tab is gated on (#811).
    // PublicRead is safe: PackageManifest carries no secrets. The write caps keep the partition
    // non-writable for every non-System identity (System bypasses the evaluator, so the
    // installer's own record writes are unaffected).
    private static MeshNode CreateInstalledPartitionPolicy() =>
        new("_Policy", PackageInstaller.InstalledPartition)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            Content = new PartitionAccessPolicy
            {
                PublicRead = true,
                Create = false,
                Update = false,
                Delete = false,
                Comment = false,
                Thread = false
            }
        };
}
