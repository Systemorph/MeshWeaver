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
            .AddMeshNodes(CreateRegistryCredentialNodeType())
            // Infrastructure credential, never pickable content.
            .AddAutocompleteExcludedTypes(PluginRegistryCredentials.NodeType)
            // The build-completion subscriber. A mesh-scoped SINGLETON, so its subscriptions live
            // and die with the mesh rather than surviving disposal into the next test
            // (Doc/Architecture/NoStaticState). The IHostedService registration is what STARTS it —
            // the host only starts services registered under the interface, so the bare singleton
            // alone would leave the build-node subscription never opened (Copilot catch). Forwarded
            // to the same instance so start/stop and the mesh singleton are one object.
            .ConfigureServices(services => services
                .AddSingleton<PluginUpdateWatcher>()
                .AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                    sp => sp.GetRequiredService<PluginUpdateWatcher>())
                // Resolves an inbound instance key to its instance + grant for the /api/plugins
                // surface. Mesh-scoped singleton so its short-lived cache dies with the mesh
                // (Doc/Architecture/NoStaticState) — a revoked grant must not outlive a test either.
                .AddSingleton<InstanceRegistryAuthenticator>()
                // Registration bootstrap keys (mwr_) — minted on the admin surface, validated by
                // the /api/instances/register endpoint. Mesh-scoped like everything above.
                .AddSingleton<RegistrationKeyService>()
                .AddSingleton<InstanceRegistrationClient>()
                // First-startup auto-registration: when PluginCatalog:BootstrapKey is configured
                // and no instance key is stored yet, register this installation at the configured
                // registry and persist the issued key. Same two-registration idiom as the watcher —
                // the IHostedService forward is what STARTS it.
                .AddSingleton<InstanceAutoRegistrationService>()
                .AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                    sp => sp.GetRequiredService<InstanceAutoRegistrationService>())
                // Resolves the effective registry token: configured token, else the stored
                // auto-registration credential (decrypted), else empty.
                .AddSingleton<RegistryTokenResolver>()
                // Re-runs the install hooks for ALREADY-installed packages once at startup. Packages
                // installed before hooks existed never registered their agent/skill sources, so on a
                // live instance every user's picker is missing them — and nothing else would fix it
                // until the next package update. Idempotent: on a repaired instance it writes nothing.
                .AddSingleton<InstalledPackageRepairService>()
                .AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                    sp => sp.GetRequiredService<InstalledPackageRepairService>())
                // THE DEFAULT INSTALL: every package declaring `preInstalled` in its manifest is
                // installed (and re-published) on this instance at startup. Mesh-scoped singleton +
                // the IHostedService forward that actually STARTS it — the same two-registration
                // idiom as the watcher above; the bare singleton alone would never run.
                .AddSingleton<PreInstalledPackageService>()
                .AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                    sp => sp.GetRequiredService<PreInstalledPackageService>()))
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
    // About tab (read-only installed inventory; provisioning is the Store's job), and a registry
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
            .WithType(typeof(PluginManifest), nameof(PluginManifest))
            .WithType(typeof(PluginRegistryCredential), nameof(PluginRegistryCredential));

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

    // The auto-registration credential (the instance key this installation received when it
    // registered itself at a registry). Admin-partition nodes; the partition's access control is
    // the gate, and the key itself is enc:-protected at rest when a master key is configured.
    private static MeshNode CreateRegistryCredentialNodeType() => new(PluginRegistryCredentials.NodeType)
    {
        Name = "Plugin Registry Credential",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(s => s.WithContentType<PluginRegistryCredential>()),
    };

    // Read-only, world-readable policy for the install-records partition — the same shape every
    // other built-in catalog ships (BuiltInAgentProvider / BuiltInSkillProvider / the model
    // catalog). The records are written exclusively under ImpersonateAsSystem (PackageInstaller),
    // so no creator grant is ever minted, and a platform admin's Admin/_Access grant is scoped to
    // the Admin partition — without this policy NO real signed-in principal holds Read on
    // "Plugins", and the installed-state query every catalog surface issues
    // (CatalogLayoutAreas.ObserveInstalled, `path:Plugins scope:children`) is denied for every
    // real principal, platform admins included (#811).
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
