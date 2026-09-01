using MeshWeaver.Domain;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
            .AddMeshNodes(CreateSigningKeyNodeType())
            .AddMeshNodes(CreateModuleDiscoveryNodeType())
            .AddMeshNodes(CreateDefaultInstallLedgerNodeType())
            // Infrastructure credential, never pickable content.
            .AddAutocompleteExcludedTypes(PluginRegistryCredentials.NodeType)
            // The registry's token signing key — infrastructure, never pickable content.
            .AddAutocompleteExcludedTypes(SyncTokenSigningKeys.NodeType)
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
                // 🚨 The watcher above is the REGISTRY's input — it needs a GitHub webhook and a
                // catalog node naming a source repo, so on a registry-only consumer it opens zero
                // subscriptions and is live-and-inert. That was the whole of #1318: such an
                // installation could install but could never learn there was anything to install.
                // This is the CONSUMER's input — it READS the registry feed it already
                // authenticates to, at boot, instead of waiting for a webhook that cannot arrive.
                // Both are registered unconditionally and both are inert on a deployment that is
                // not the shape they serve; they hand the same decision to PackageUpdateReconciler.
                .AddSingleton<RegistryUpdateReconciler>()
                .AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                    sp => sp.GetRequiredService<RegistryUpdateReconciler>())
                // Resolves an inbound instance key to its instance + grant for the /api/plugins
                // surface. Mesh-scoped singleton so its short-lived cache dies with the mesh
                // (Doc/Architecture/NoStaticState) — a revoked grant must not outlive a test either.
                .AddSingleton<InstanceRegistryAuthenticator>()
                // Issues and revokes the SYNC LICENCE that authenticator then reads. One writer for
                // every issuer (the admin tab, a fulfilled order, a redeemed coupon, an automated
                // provision), so a licence carries its terms — and its attribution — however it was
                // granted. Mesh-scoped like the authenticator it feeds.
                .AddSingleton<SyncLicenseService>()
                // Mints (once, per registry) and rotates the key that signs short-lived sync access
                // tokens. Mesh-scoped so its cache dies with the mesh, like the authenticator.
                .AddSingleton<SyncTokenSigningKeyService>()
                // The SECOND issuer's key source (#2483): GitHub's OIDC JWKS, read once an hour and
                // shared. Mesh-scoped for the same reason as everything above — a key set held in a
                // static field would outlive the mesh and bleed across tests and deployments. A
                // fetch that fails FAILS CLOSED: the observable errors, so the authenticator answers
                // "undetermined" (503 + Retry-After) rather than "unknown token", and never accepts.
                .AddSingleton<GitHubOidcKeyService>()
                // The plan ladder (Admin/Tiers/*) a plan-scoped grant entry is decided against —
                // read once per minute, on the caller, by the authenticator above. Mesh-scoped so
                // the cache dies with the mesh.
                .AddSingleton<PlanTierLadder>()
                // The plan on the instance record — promoted by a global admin, read by every
                // registry decision (#2804).
                .AddSingleton<InstancePlanService>()
                // The consent that gates an open registration, and the live views the Hosting
                // app renders it from (#2804 program, slice 3).
                .AddSingleton<InstanceConsentService>()
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
                .AddSingleton<InstanceComboReader>()
                // The runtime modules/ writer (#1664 step 7) — lands a compiled module's
                // assemblies + activation record; restart-as-activation. Inert until called:
                // Slice C's PackageInstaller binary branch is its caller. Mesh-scoped so its
                // bounded IO pool dies with the mesh.
                // 🚨 Constructed with the resolved MODULE ROOT, never the default: the default is
                // AppContext.BaseDirectory, which is READ-ONLY in the container, so a landing there
                // fails with a denied-path error the publisher sees as HTTP 409. See ModuleRoot.
                .AddSingleton(sp => new ModuleLandingService(
                    sp.GetService<ILogger<ModuleLandingService>>(),
                    ModuleRoot.Resolve(sp.GetService<IConfiguration>())))
                // The restart-as-activation READER (#1979): which landed modules are not loaded in
                // THIS process. Registered beside the writer and rooted at the same resolved
                // module root — a reader looking at a different directory than the writer is how
                // "installed, and nothing happened" becomes unexplainable. A plain singleton: it
                // starts nothing and writes nothing, so an instance that never asks pays nothing.
                // 🚨 It is registered rather than merely constructible so a NodeType's layout area
                // can resolve it from hub.ServiceProvider — the Store's install step is the
                // surface where the missing last step is actually met.
                .AddSingleton(sp => new PendingModuleActivations(
                    ModuleRoot.Resolve(sp.GetService<IConfiguration>())))
                // The COUNT that proves the distribution lane works (#1782 gap 4). Adoption's only
                // evidence used to be a log line, and the most important miss — "the registry does
                // not advertise this package for my lane" — had no line at all. With lazy
                // compile-on-access replacing instance pre-bake (#1746), a miss is absorbed so
                // completely that the lane can go dark while every surface looks like a healthy
                // day; that is exactly what 2026-08-20 was. A plain singleton, process-scoped and
                // bounded: a diagnostic, never a source of truth.
                .AddSingleton<BundleAdoptionLedger>()
                // 🚨 THE ENTITLEMENT ANCHOR (#1782 gap 2) — the registry's own catalog, read as the
                // authority on which SOURCE carries which package. A local install record is a
                // cache of that binding, and a cache miss must send the question upstream rather
                // than answer "not entitled". Singleton because it keeps the last successful
                // observation, which is what keeps a previously observed entitlement working while
                // the registry is unreachable.
                .AddSingleton(sp => new PackageOriginAnchor(
                    sp.GetRequiredService<IMessageHub>(),
                    sp.GetService<IConfiguration>() ?? new ConfigurationBuilder().Build(),
                    sp.GetService<ILoggerFactory>()))
                // …and the record that makes a degraded entitlement answer legible. Every refusal
                // on the bundle routes is byte-identical on the wire (#1777), which is right for
                // the caller and blind for the operator: "not granted" and "I could not reach the
                // registry to find out" leave the same trace. Bounded, process-scoped diagnostic.
                .AddSingleton<PackageEntitlementLedger>())
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
            .WithType(typeof(SyncTokenSigningKey), nameof(SyncTokenSigningKey))
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
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(s => s.WithContentType<PluginRegistryCredential>()),
    };

    // The HMAC key this registry signs short-lived sync access tokens with. Sits beside the registry
    // credential for the same reasons: an Admin-partition secret whose partition access control is
    // the gate, enc:-protected at rest, and never content a user creates. ONE node per registry at a
    // FIXED path — that is what lets two replicas racing to mint it collide and resolve to one key.
    private static MeshNode CreateSigningKeyNodeType() => new(SyncTokenSigningKeys.NodeType)
    {
        Name = "Sync Token Signing Key",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(s => s.WithContentType<SyncTokenSigningKey>()),
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
            ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
            HubConfiguration = config => config
                .AddMeshDataSource(s => s.WithContentType<DefaultInstallLedger>()),
        };

    // Read-only, world-readable policy for the install-records partition — the same shape every
    // other built-in catalog ships (BuiltInAgentProvider / BuiltInSkillProvider / the model
    // catalog). The records are written exclusively as System (PackageInstaller), so no creator
    // grant is ever minted, and a platform admin's Admin/_Access grant is scoped to the Admin
    // partition — without this policy NO real signed-in principal holds Read on "Plugins", and the
    // installed-state query every catalog surface issues (CatalogLayoutAreas.ObserveInstalled,
    // `path:Plugins scope:children`) is denied for every real principal, platform admins included
    // (#811). PublicRead is safe: PackageManifest carries no secrets. The write caps keep the
    // partition non-writable for every non-System identity (System bypasses the evaluator, so the
    // installer's own record writes are unaffected).
    //
    // 🚨 THIS STATIC NODE IS NOT ENOUGH ON ITS OWN, and that is the whole of #1950. It covers the
    // LIVE evaluator — which reads it happily, so every in-memory test passed — but it has no row
    // anywhere, and Postgres pre-filters partition-scoped queries by public.partition_access, whose
    // rows come from rebuild_user_effective_permissions() folding mesh_nodes for
    // node_type='PartitionAccessPolicy' AND id='_Policy'. So on a PG mesh the partition was
    // invisible to every query for every principal. PackageInstaller.EnsureRecordsPartitionReadable
    // writes the DURABLE twin (at boot and on every install); this stays because it covers the
    // window before that write lands and the hosts with no SQL side at all. Both come from ONE
    // definition below so the two can never drift into disagreeing about the partition's access.
    private static MeshNode CreateInstalledPartitionPolicy() =>
        PackageInstaller.InstalledPartitionPolicy();
}
