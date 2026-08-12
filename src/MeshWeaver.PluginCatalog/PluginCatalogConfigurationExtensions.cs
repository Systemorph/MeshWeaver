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
            .AddMeshNodes(CreateModuleDiscoveryNodeType())
            .AddMeshNodes(CreateDefaultInstallLedgerNodeType())
            // Infrastructure credential, never pickable content.
            .AddAutocompleteExcludedTypes(PluginRegistryCredentials.NodeType)
            // Bookkeeping, never pickable content — same reason as the credential above.
            .AddAutocompleteExcludedTypes(InstanceAutoRegistrationService.LedgerNodeType)
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
                // 🚨 THE ONE DEFAULT-INSTALL PATH. Two phases in one service, deliberately: phase 1
                // auto-registers this installation at the configured registry when
                // PluginCatalog:BootstrapKey is set and no instance key is stored yet, and phase 2
                // installs what this deployment comes up with — the packages declaring
                // `preInstalled` (the platform baseline, every boot) plus the operator's
                // InstallByDefault seed (a fresh installation only), in dependency order.
                // They are ONE service because phase 2 needs the key phase 1 mints; two hosted
                // services would race each other over the same partitions. Same two-registration
                // idiom as the watcher — the IHostedService forward is what STARTS it.
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
                // Auto-discovery of a configured repo's modules (#833). Registered LAST of the
                // package services because its boot scan deliberately waits for
                // InstanceAutoRegistrationService.Completed — both touch the same partitions. Inert
                // unless a source sets AutoDiscover, so it costs an unconfigured instance nothing.
                // Same two-registration idiom: the IHostedService forward is what STARTS it.
                .AddSingleton<ModuleDiscoveryService>()
                .AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                    sp => sp.GetRequiredService<ModuleDiscoveryService>())
                // Reads this instance's COMBO — every module it carries with its source and pinned
                // ref, folding the {Space}/_GitSync and Plugins/{id} shapes into one list. The input
                // the candidate-release deploy gate verifies an image against
                // (Doc/Architecture/CandidateReleaseProtocol). A plain singleton, NOT a hosted
                // service: it is a pull-on-demand READER — it starts nothing, subscribes to nothing,
                // and writes nothing, so it costs an instance that never calls it exactly nothing.
                .AddSingleton<InstanceComboReader>())
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
            .WithType(typeof(PluginRegistryCredential), nameof(PluginRegistryCredential))
            .WithType(typeof(ModuleDiscovery), nameof(ModuleDiscovery))
            .WithType(typeof(DefaultInstallLedger), nameof(DefaultInstallLedger));

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

    // What a configured plugin repo carries and what this instance did with it (#833). An
    // Admin-partition record, like the BuildCompletion node it sits beside: it describes modules
    // that may not exist here at all, so it cannot live on any of them. Not pickable content —
    // it is infrastructure a platform admin reads, never something a user creates.
    private static MeshNode CreateModuleDiscoveryNodeType() => new(ModuleDiscovery.NodeType)
    {
        Name = "Module Discovery",
        Icon = "/static/NodeTypeIcons/box.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "create" },
        HubConfiguration = config => config
            .AddDefaultLayoutAreas()
            .AddMeshDataSource(s => s.WithContentType<ModuleDiscovery>()),
    };

    // 🚨 The default-install ledger's own NodeType. Registering the CONTENT type
    // (AddPluginCatalogTypes' WithType<DefaultInstallLedger>) is only half of it: CreateNode
    // validates node.NodeType against the registered NodeType MeshNodes, so without this the
    // ledger write died every boot with "NodeType 'DefaultInstallLedger' is not registered"
    // (memex 2026-08-10, 07:16:45 and 11:46:24). RecordSeeded swallows that as a warning —
    // correctly, since a lost ledger must not fail a boot — so the failure was silent, and the
    // ledger stayed permanently empty. Consequence: SeedLedger() always answered "nothing seeded",
    // so EVERY boot re-ran the FULL default install (upserting every plugin partition root) rather
    // than the intended repair-only pass, and the "a failed package is retried next boot" design
    // could never distinguish a repair from a re-run of work already done.
    // Same trap ApiToken and MeshWeaverInstance hit — the content type is not the node type.
    private static MeshNode CreateDefaultInstallLedgerNodeType() =>
        new(InstanceAutoRegistrationService.LedgerNodeType)
        {
            Name = "Default Install Ledger",
            Icon = "/static/NodeTypeIcons/box.svg",
            // Bookkeeping, not a satellite of anything: it is a single node in the Plugins
            // partition, addressed by a fixed path, exactly like the credential node type.
            IsSatelliteType = false,
            ExcludeFromContext = new HashSet<string> { "search", "create" },
            HubConfiguration = config => config
                .AddMeshDataSource(s => s.WithContentType<DefaultInstallLedger>()),
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
