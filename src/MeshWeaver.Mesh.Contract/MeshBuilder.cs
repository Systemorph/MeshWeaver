using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Domain;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Features;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting")]
namespace MeshWeaver.Mesh;

/// <summary>
/// Builder for configuring a mesh instance including hub configuration, services, and mesh nodes.
/// </summary>
public record MeshBuilder
{
    /// <summary>
    /// Initializes a new instance of the MeshBuilder.
    /// </summary>
    /// <param name="ServiceConfig">Action to configure services in the DI container.</param>
    /// <param name="Address">The address of the mesh hub.</param>
    public MeshBuilder(Action<Func<IServiceCollection, IServiceCollection>> ServiceConfig, Address Address)
    {
        this.ServiceConfig = ServiceConfig;
        this.Address = Address;
        // 🚨 "No one must ever publish from main hub": the router names its spokesman up front,
        // so infrastructure that would otherwise post through the router (Workspace's recycle
        // announcement) has a sanctioned non-router carrier — the same nodeops execution hub
        // every node-CRUD path already hops onto. Resolve returns null only during teardown,
        // which is exactly when the caller's own fallback applies.
        ConfigureHub(config => config.Set(
            new RouterCarrier(static router => router.NodeOperationExecutionHub())));
        Register();
    }

    private List<MeshNode> MeshNodes { get; } = new();

    /// <summary>
    /// The deployment's configuration, when the caller supplied it — the surface an
    /// assembly-attribute module needs to answer a question whose answer is a CONFIG value.
    ///
    /// <para>🚨 A module contributes through <see cref="MeshNodeProviderAttribute"/> at INSTALL
    /// time, and until now that was a blind spot: <c>MeshWeaver.Social</c> records it as
    /// "there is no IConfiguration instance at install time", which is why it binds through the
    /// options pipeline instead. Options work when the answer is needed at RESOLVE time. They do
    /// not work when it is needed to BUILD something — e.g. whether a type-definition node is
    /// <c>IsDefinitionOnly</c>, an <c>init</c> property fixed when the node is constructed, and
    /// getting it wrong makes a partition root permanently unrecoverable (#902).</para>
    ///
    /// <para><c>null</c> when nothing supplied one — a bespoke host, a test fixture, or a direct
    /// <see cref="InstallAssemblies"/>. A module reading this MUST treat null as "not configured"
    /// and fall back to the same default it would have used with an absent key, never to a guess:
    /// the value it is deciding is usually one where a wrong answer is silent.</para>
    /// </summary>
    public IConfiguration? Configuration { get; private set; }

    /// <summary>
    /// Whether this instance has NO storage yet and is awaiting the setup wizard (#2550).
    ///
    /// <para>🚨 A host that reads this true must serve the SETUP surface and nothing else. It must
    /// not invent a storage backend to get going: a guessed backend writes real data somewhere
    /// nobody chose — typically the container's ephemeral working directory, which reads back fine
    /// for minutes and is gone at the next roll (issue #435's shape). Awaiting setup is a state to
    /// SERVE, not a gap to paper over.</para>
    ///
    /// <para>False for every deployment configured through appsettings, which is all of them until
    /// an operator installs an empty image on purpose.</para>
    /// </summary>
    public bool IsAwaitingSetup { get; private set; }

    /// <summary>Records that this instance has no storage configured and no completed setup
    /// manifest. One-way: nothing clears it, because the cure is a restart with an answer.</summary>
    public MeshBuilder MarkAwaitingSetup()
    {
        IsAwaitingSetup = true;
        return this;
    }

    /// <summary>
    /// Supplies the deployment configuration that <see cref="Configuration"/> exposes to
    /// attribute-carried module contributions. Called for you by
    /// <c>MeshBuilderModuleActivation.InstallConfiguredModules</c>, which already holds it;
    /// a bespoke host that installs modules by hand can call it directly.
    /// </summary>
    /// <param name="configuration">The configuration to expose. Never null.</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder WithConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Configuration = configuration;
        return this;
    }

    /// <summary>
    /// Resolves one <c>Modules:Assemblies</c> entry to an assembly path. Rooted paths pass
    /// through; relative entries probe the <c>modules/&lt;name&gt;/&lt;entry&gt;</c> publish
    /// layout FIRST (the modules-folder lane, #1644 — a module published beside the app wins so
    /// flipping its ProjectReference off changes nothing for the deployment), then fall back to
    /// the classic BaseDirectory-relative location (the double-shipped transition state, and
    /// every module that still rides the app closure).
    /// </summary>
    public static string ResolveModulePath(string entry) => ResolveModulePath(entry, null);

    /// <summary>
    /// As <see cref="ResolveModulePath(string)"/>, but probing a LANDED module root first.
    ///
    /// <para>🚨 <b>Two <c>modules/</c> trees exist and both are legitimate</b>, which is why this
    /// takes a root rather than moving the one it had. The image publishes baseline packs into its
    /// own <c>modules/</c> beside the app; a module the registry LANDS at runtime is written to the
    /// deployment's writable, pod-SHARED root instead (see <c>ModuleRoot</c>) — because
    /// <c>AppContext.BaseDirectory</c> is read-only in the container and, even where it is not, a
    /// per-pod copy would be invisible to every other replica.</para>
    ///
    /// <para>Order is landed → image → app closure, and it matters: a landed module is the one an
    /// operator just published, so it must win over a stale baseline copy of the same name. When
    /// <paramref name="moduleRoot"/> is null or already the app directory this is byte-for-byte
    /// <see cref="ResolveModulePath(string)"/> — the unconfigured deployment is untouched.</para>
    /// </summary>
    public static string ResolveModulePath(string entry, string? moduleRoot)
    {
        if (Path.IsPathRooted(entry))
            return entry;
        var baseDirectory = AppContext.BaseDirectory;
        var name = Path.GetFileNameWithoutExtension(entry);

        if (!string.IsNullOrWhiteSpace(moduleRoot))
        {
            var landed = Path.Combine(moduleRoot, "modules", name, entry);
            if (File.Exists(landed))
                return landed;
        }

        var moduleFolderPath = Path.Combine(baseDirectory, "modules", name, entry);
        return File.Exists(moduleFolderPath)
            ? moduleFolderPath
            : Path.Combine(baseDirectory, entry);
    }

    /// <summary>
    /// Installs mesh nodes from the specified assembly locations.
    /// </summary>
    /// <param name="assemblyLocations">Paths to assemblies containing MeshNodeProviderAttribute definitions.</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder InstallAssemblies(params string[] assemblyLocations)
    {
        // A module's NATIVE payload is unreachable without this (#1728): Assembly.LoadFrom never
        // consults the module's deps.json, so nothing probes modules/<Name>/runtimes/<rid>/native/.
        // Subscribed here — before anything from a module folder is loaded — and idempotent.
        ModuleNativeAssets.EnsureRegistered();

        // 🚨 Per-module isolation (#2234). One module that cannot install against THIS build must
        // cost that module's contribution and nothing else. It used to cost the process: a landed
        // AzureFoundry built against a 9-parameter record ctor met an image carrying the
        // 8-parameter one, the MissingMethodException escaped this method, and every replacement
        // pod aborted ~2 s into boot with no application logging (the pipeline is not up yet) —
        // memex-cloud could not start a pod for ~90 minutes.
        //
        // Everything an attribute's Nodes/AddressTypes/HubConfigurations getters can THROW from is
        // materialised here, before any builder state is mutated, so a module that fails leaves no
        // half-applied configuration behind. This captures the GlobalServiceConfigurations
        // delegates a node carries as DATA — it does not invoke them. Invoking them is a separate,
        // equally hazardous step, isolated per module just below.
        var pending = new List<PendingModuleInstall>();
        var incompatible = new List<IncompatibleModule>();
        foreach (var location in assemblyLocations)
        {
            try
            {
                var assembly = Assembly.LoadFrom(location);
                var moduleAttributes = assembly.GetCustomAttributes<MeshNodeProviderAttribute>().ToArray();
                pending.Add(new PendingModuleInstall(
                    assembly,
                    moduleAttributes.SelectMany(a => a.Nodes).ToArray(),
                    moduleAttributes.SelectMany(a => a.AddressTypes).ToArray(),
                    moduleAttributes.SelectMany(a => a.HubConfigurations).ToArray(),
                    moduleAttributes.SelectMany(a => a.DefaultNodeHubConfigurations).ToArray(),
                    moduleAttributes.SelectMany(a => a.BuilderConfigurations).ToArray()));
            }
            catch (Exception exception)
            {
                incompatible.Add(ReportIncompatible(location, exception));
            }
        }

        // 🚨 A node's GlobalServiceConfigurations delegate is invoked IMMEDIATELY by
        // ConfigureServices — it runs against the live IServiceCollection, not queued for later
        // (see ConfigureServices / InstallServices below) — so it is exactly as hazardous as the
        // attribute materialisation above, and it is where BOTH real #2234 incidents actually
        // threw: the original report's stack named a GlobalServiceConfigurations callback
        // (`AzureFoundryProvidersAttribute.<get_Nodes>b__1_0(IServiceCollection)`) being CALLED,
        // and the systemorph recurrence named this exact frame
        // (`MeshBuilder.InstallServices(IEnumerable`1 nodes)`) directly. Materialising `a.Nodes`
        // above only captures the delegate; invoking it is what can throw. Folded per module, same
        // shape as the BuilderConfigurations fold below, so one module's registration failure
        // costs only that module — never the modules that load after it in this call.
        var installed = new List<PendingModuleInstall>();
        var installedNodes = new List<MeshNode>();
        foreach (var module in pending)
        {
            try
            {
                // 🚨 Materialise this module's nodes into a LOCAL buffer BEFORE touching
                // installedNodes/MeshNodes. A module can carry several nodes; if an EARLIER one's
                // config succeeds and a LATER one's throws, `.ToList()` still throws here (nothing
                // is appended), so the module stays "contributes nothing" at the node-list level
                // even though the earlier node's ConfigureServices call already mutated the live
                // IServiceCollection for real and cannot be undone — the same asymmetry the
                // BuilderConfigurations fold below already accepts (applied side effects stay
                // applied; only chain/list MEMBERSHIP is what stays consistent).
                var moduleNodes = InstallServices(module.Nodes).ToList();
                installedNodes.AddRange(moduleNodes);
                installed.Add(module);
            }
            catch (Exception exception)
            {
                incompatible.Add(ReportIncompatible(module.Assembly.Location, exception));
            }
        }
        MeshNodes.AddRange(installedNodes);

        // Only the modules that actually installed are recorded as installed. A skewed one is
        // deliberately NOT in this list: it contributes no nodes, and letting it into the in-mesh
        // compile reference set would hand every dynamic NodeType the same broken signatures.
        var assemblies = installed.Select(p => p.Assembly).ToArray();
        // Record every installed module for the runtime surfaces that must SEE modules the way
        // they see the platform: the in-mesh compile reference set (a module leaving the publish
        // closure leaves TRUSTED_PLATFORM_ASSEMBLIES, so compilation composes TPA + these) and
        // the bake fingerprint (a module upgrade invalidates baked builds that could reference
        // it). Registered even while a module still ALSO rides the app closure — the surfaces
        // dedupe by identity.
        ConfigureServices(services =>
        {
            foreach (var assembly in assemblies)
                services.AddSingleton(new InstalledModuleAssembly(assembly));
            return services;
        });

        // Register address types from attributes
        var addressTypes = installed.SelectMany(p => p.AddressTypes).ToArray();
        if (addressTypes.Length > 0)
        {
            ConfigureHub(config =>
            {
                config.TypeRegistry.WithTypes(addressTypes);
                return config;
            });
        }

        // Attribute-carried hub configuration — the surfaces a boot-loaded pack needs beyond
        // root DI: the mesh hub's own configuration and the every-per-node-hub chain
        // (Courses/Observability-shaped packs register types + default areas there).
        foreach (var hubConfiguration in installed.SelectMany(p => p.HubConfigurations))
            ConfigureHub(hubConfiguration);
        foreach (var nodeHubConfiguration in installed.SelectMany(p => p.DefaultNodeHubConfigurations))
            ConfigureDefaultNodeHub(nodeHubConfiguration);

        // Attribute-carried BUILDER configuration — the full-surface hook. Applied last so a
        // builder-level hook observes the attribute's own nodes/services, mirroring the order a
        // compiled-in caller would get from `builder.InstallAssemblies(...).AddX()`. MeshBuilder
        // methods mutate this instance and return it, so the fold cannot lose configuration.
        //
        // Folded per MODULE rather than over one flat list: these run arbitrary module code, so a
        // throw here is the same hazard as the materialisation above and must cost only its own
        // module. The fold keeps the builder from the last SUCCESSFUL configuration, so a module
        // that throws midway cannot strand the chain.
        var result = this;
        foreach (var module in installed)
        {
            try
            {
                result = module.BuilderConfigurations.Aggregate(result, (builder, configure) => configure(builder));
            }
            catch (Exception exception)
            {
                incompatible.Add(ReportIncompatible(module.Assembly.Location, exception));
            }
        }

        // 🚨 Registered AFTER the fold, not before it. A module can fail in either half — while its
        // contributions are materialised, or while its BuilderConfigurations run — and registering
        // early captured only the first. The second would have been written to stderr and then
        // dropped, so /health and RequiredModuleStatus would report a replica missing that module's
        // features as healthy: the exact invisible-skip this record exists to prevent, reintroduced
        // one code path over.
        if (incompatible.Count > 0)
        {
            result.ConfigureServices(services =>
            {
                foreach (var module in incompatible)
                    services.AddSingleton(module);
                return services;
            });
        }
        return result;
    }

    /// <summary>
    /// One module's contributions, materialised before any builder state is touched.
    /// </summary>
    private sealed record PendingModuleInstall(
        Assembly Assembly,
        IReadOnlyCollection<MeshNode> Nodes,
        IReadOnlyCollection<KeyValuePair<string, Type>> AddressTypes,
        IReadOnlyCollection<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations,
        IReadOnlyCollection<Func<MessageHubConfiguration, MessageHubConfiguration>> DefaultNodeHubConfigurations,
        IReadOnlyCollection<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations);

    /// <summary>
    /// Records a module that could not install, and writes it to stderr.
    ///
    /// <para>🚨 stderr, not a logger: this runs BEFORE the logging pipeline exists, which is
    /// exactly why #2234's crash left a container log containing only the createdump DSO listing
    /// and cost most of a day to diagnose. The one channel that works at this point is the one the
    /// container captures.</para>
    /// </summary>
    private static IncompatibleModule ReportIncompatible(string entry, Exception exception)
    {
        var module = IncompatibleModule.From(entry, exception);
        Console.Error.WriteLine($"[MeshWeaver.Mesh.IncompatibleModule] {module.Report()}");
        return module;
    }

    private IEnumerable<MeshNode> InstallServices(IEnumerable<MeshNode> nodes)
    {
        foreach (var meshNode in nodes)
        {
            foreach (var config in meshNode.GlobalServiceConfigurations)
            {
                // 🚨 Isolate the module's HOSTED SERVICES at activation (#2449). Everything else
                // this class isolates is an INSTALL-time failure; a hosted service registered here
                // only leaves a descriptor behind, and its constructor runs later — when the
                // generic host resolves IHostedService[], outside this method entirely and
                // all-or-nothing. One unsatisfiable constructor there aborted the whole portal
                // (memex-cloud, 2026-08-26: every replacement pod SIGABRTed at boot and the
                // rollout wedged for hours). Wrapping only what THIS module's configuration adds
                // keeps platform-registered hosted services fatal, exactly as they should be.
                var scoped = config;
                ConfigureServices(services => IsolateModuleHostedServices(meshNode, scoped, services));
            }
            yield return meshNode;
        }
    }

    /// <summary>
    /// Runs one module's service configuration and replaces any <c>IHostedService</c> it registered
    /// with an <see cref="IsolatedModuleHostedService"/> bound to that module.
    ///
    /// <para>The scoping is by descriptor IDENTITY and deliberate: only descriptors that were not
    /// present before this module's configuration ran are this module's. A registration that was
    /// already there belongs to the platform or to an earlier module and is left alone — this must
    /// never become a blanket catch over host startup.</para>
    /// </summary>
    internal static IServiceCollection IsolateModuleHostedServices(
        MeshNode meshNode,
        Func<IServiceCollection, IServiceCollection> configure,
        IServiceCollection services)
    {
        // 🚨 Scope by IDENTITY, not by index. An index snapshot assumes the configuration only
        // APPENDS; one that removes, inserts or replaces a descriptor ahead of the mark shifts
        // everything after it, and the loop would then wrap a PLATFORM (or earlier module's)
        // hosted service — silently converting a fatal platform failure into a skipped one, which
        // is the single thing this isolation must never do. Recording which descriptors existed
        // beforehand costs one set and is immune to that.
        var preExisting = new HashSet<ServiceDescriptor>(services, ReferenceEqualityComparer.Instance);
        var result = configure(services);

        // A configuration that swapped the collection out from under us cannot be scoped at all;
        // leave it exactly as it is rather than guess.
        if (!ReferenceEquals(result, services))
            return result;

        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(IHostedService))
                continue;
            if (preExisting.Contains(descriptor))
                continue;   // present before this module ran — platform or an earlier module

            var moduleName = meshNode.Path ?? meshNode.Id ?? "(unnamed module)";
            var resolve = ResolverFor(descriptor);
            if (resolve is null)
                continue;   // an instance registration has nothing to activate

            services[i] = ServiceDescriptor.Describe(
                typeof(IHostedService),
                sp => new IsolatedModuleHostedService(
                    moduleName,
                    resolve,
                    sp,
                    (sp.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
                        ?.CreateLogger("MeshWeaver.Mesh.IncompatibleModule")),
                descriptor.Lifetime);
        }

        return result;
    }

    /// <summary>How the wrapped service is produced, deferred so the failure happens inside
    /// <see cref="IsolatedModuleHostedService.StartAsync"/> where it can be isolated — never while
    /// the host is materialising the <c>IHostedService[]</c>.</summary>
    private static Func<IServiceProvider, object>? ResolverFor(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationFactory is { } factory)
            return factory;
        if (descriptor.ImplementationType is { } type)
            return sp => ActivatorUtilities.CreateInstance(sp, type);
        return null;    // ImplementationInstance: already constructed, nothing can fail to activate
    }


    private List<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfiguration { get; } = [AddMesh];

    /// <summary>
    /// Adds configuration to the mesh hub.
    /// </summary>
    /// <param name="hubConfiguration">Function to configure the message hub.</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder ConfigureHub(
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
    {
        HubConfiguration.Add(hubConfiguration);
        return this;
    }

    private List<Func<MeshConfiguration, MeshConfiguration>> MeshConfiguration { get; } = new();

    private List<Func<MessageHubConfiguration, MessageHubConfiguration>> DefaultNodeHubConfiguration { get; } = new();

    /// <summary>
    /// Configures the default hub configuration that will be applied to all node hubs.
    /// Use this for settings like content collections (e.g., logos) that should be available everywhere.
    /// </summary>
    public MeshBuilder ConfigureDefaultNodeHub(
        Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
    {
        DefaultNodeHubConfiguration.Add(configuration);
        return this;
    }

    /// <summary>
    /// Configures services in the dependency injection container.
    /// </summary>
    /// <param name="configuration">Function to configure services.</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder ConfigureServices(Func<IServiceCollection, IServiceCollection> configuration)
    {
        ServiceConfig.Invoke(configuration);
        return this;
    }
    private Action<Func<IServiceCollection, IServiceCollection>> ServiceConfig { get; init; }

    /// <summary>
    /// Gets the address of the mesh hub.
    /// </summary>
    public Address Address { get; init; }

    /// <summary>
    /// Configures the mesh settings.
    /// </summary>
    /// <param name="configuration">Function to configure the mesh.</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder ConfigureMesh(Func<MeshConfiguration, MeshConfiguration> configuration)
    {
        MeshConfiguration.Add(configuration);
        return this;
    }

    private void Register()
    {
        // Create mesh-level type registry for polymorphic serialization
        // Hub-level type registries will inherit from this via ParentServiceProvider
        var meshTypeRegistry = MessageHubExtensions.CreateTypeRegistry();

        // Capture the list references - will be populated by builder calls later
        // The lambdas are evaluated when services are resolved (after all builder calls)
        var defaultNodeHubConfigs = DefaultNodeHubConfiguration;
        var meshTypeRegs = MeshTypeRegistrations;
        var excludedTypes = AutocompleteExcludedTypes;
        var accessConfig = NodeTypeAccessConfig;
        var routingRules = QueryRoutingRules;
        var streamRoutedTypes = StreamRoutedAddressTypes;
        var clientHostedTypes = ClientHostedAddressTypes;

        ConfigureServices(services => services
            .AddSingleton(_ =>
            {
                // Evaluate defaultNodeHubConfigs at service resolution time, not at Register() time
                Func<MessageHubConfiguration, MessageHubConfiguration>? combinedDefaultConfig =
                    defaultNodeHubConfigs.Count > 0
                        ? config => defaultNodeHubConfigs.Aggregate(config, (c, f) => f(c))
                        : null;
                return new MeshConfiguration(
                    // Internal-only list — MeshConfiguration uses it to compute
                    // derived lazies (ContextExcludedTypes / SatelliteNodeTypes);
                    // no public property exposes it. Application code reads
                    // static nodes via serviceProvider.EnumerateStaticNodes().
                    MeshNodes,
                    combinedDefaultConfig,
                    autocompleteExcludedNodeTypes: excludedTypes.Count > 0 ? excludedTypes : null,
                    queryRoutingRules: routingRules,
                    streamRoutedAddressTypes: streamRoutedTypes,
                    nodeTypeGates: accessConfig.BuildGates())
                {
                    // An init property, not a ctor argument — see MeshConfiguration for why a
                    // trailing optional parameter would still be a binary break for a module that
                    // is already published.
                    ClientHostedAddressTypes = clientHostedTypes,
                };
            })
            // Static nodes registered via AddMeshNodes(...) flow as an
            // IStaticNodeProvider. Application code reads them via
            // serviceProvider.EnumerateStaticNodes() — there is no Nodes
            // dictionary on MeshConfiguration. Last-write-wins by Path is
            // applied at iteration time inside the provider.
            .AddSingleton<IStaticNodeProvider>(new StaticMeshNodeListProvider(MeshNodes))
            .AddSingleton<ITypeRegistry>(_ =>
            {
                // Register core mesh types on the shared registry so they're available to ALL hubs
                // This ensures proper $type serialization across hub boundaries
                meshTypeRegistry.WithType(typeof(MeshNode), nameof(MeshNode));
                meshTypeRegistry.WithType(typeof(MeshNodeState), nameof(MeshNodeState));
                meshTypeRegistry.WithType(typeof(PingRequest), nameof(PingRequest));
                meshTypeRegistry.WithType(typeof(PingResponse), nameof(PingResponse));
                meshTypeRegistry.WithType(typeof(CreateNodeRequest), nameof(CreateNodeRequest));
                meshTypeRegistry.WithType(typeof(CreateNodeResponse), nameof(CreateNodeResponse));
                meshTypeRegistry.WithType(typeof(DeleteNodeRequest), nameof(DeleteNodeRequest));
                meshTypeRegistry.WithType(typeof(DeleteNodeResponse), nameof(DeleteNodeResponse));
                meshTypeRegistry.WithType(typeof(ExecuteScriptRequest), nameof(ExecuteScriptRequest));
                meshTypeRegistry.WithType(typeof(ExecuteScriptResponse), nameof(ExecuteScriptResponse));

                // Register additional types added via WithMeshType()
                foreach (var (type, name) in meshTypeRegs)
                    meshTypeRegistry.WithType(type, name);

                return meshTypeRegistry;
            })
            .AddSingleton(BuildHub)
            .AddSingleton<AccessService>()
            // Mesh-ROOT "delete wins" tombstone + subtree-deletion scope. ONE instance,
            // registered at the root deliberately: the delete handler resolves it off a
            // hub ServiceProvider (which falls back to the root) while the
            // SubtreeDeletionGuardStorageAdapter resolves it inside the persistence
            // container — a hub-level registration (the previous home, in AddGraph)
            // created a SECOND instance there, so the guard checked a registry no delete
            // ever opened a scope on and silently passed every write under a subtree
            // being deleted (#839's write-guard test caught it).
            .AddSingleton<Services.RecentlyDeletedRegistry>()
            // The SAME instance, surfaced to the message pipeline (which sits below
            // MeshWeaver.Mesh.Contract in the reference graph and therefore cannot see the
            // registry type). MessageService reads it to classify a delivery abandoned by a
            // dying hub: "the node was DELETED" is an authoritative NotFound, everything else
            // stays the transient ShuttingDown. See IAddressTombstones for why (#1029).
            .AddSingleton<IAddressTombstones>(sp => sp.GetRequiredService<Services.RecentlyDeletedRegistry>())
            // Mesh-ROOT durable-version high-water for the post-commit flush, registered at the
            // root for the SAME reason as the tombstone registry above: the flush is a mesh-level
            // singleton while its reader — the per-node persistence sampler's save handler — runs
            // on the owner hub, and a hub-level registration would give each side its own instance.
            // Collapses the two durable-write routes a cross-hub patch used to take (#1249).
            .AddSingleton<Services.PostCommitFlushRegistry>()
            // Controlled I/O pools — mesh-scoped governor over the shared
            // ThreadPool for genuinely-async / sync-blocking leaves (file system,
            // blob, …). Resolved by leaf adapters via IoPoolRegistry; dies with
            // the mesh. See Doc/Architecture/ControlledIoPooling.md.
            .AddIoPools()
            // The deployment's declared features (Features:Flags:*) — the per-environment switch
            // that also carries what this environment pre-installs. Mesh-scoped for the same
            // reason the pools are: it holds live state (a BehaviorSubject + a configuration
            // reload registration) and must die with the mesh. TryAdd so a host or a test can
            // supply its own reader. See Doc/Architecture/EnvironmentComposition.
            .AddFeatureFlags()
            );

        IReadOnlyCollection<Func<MeshConfiguration, MeshConfiguration>> meshConfig = MeshConfiguration;

        ConfigureHub(conf => conf.WithRoutes(routes =>
                // Observable-shaped handler — no Task<T>, no .FirstAsync().ToTask()
                // at the call site. The framework bridges once at the rule-chain
                // edge inside RouteConfiguration.WithHandler. Per
                // Doc/Architecture/AsynchronousCalls.md.
                routes.WithHandler(delivery =>
                {
                    // Compare without Host since Host tracks routing path
                    var targetWithoutHost = delivery.Target is not null ? delivery.Target with { Host = null } : null;
                    if (delivery.State != MessageDeliveryState.Submitted || targetWithoutHost == null || targetWithoutHost.Equals(Address))
                        return Observable.Return(delivery);

                    return routes.Hub.ServiceProvider.GetRequiredService<IRoutingService>()
                        .DeliverMessage(delivery.Package(routes.Hub.JsonSerializerOptions));
                }))
            .Set(meshConfig)
        );
    }

    /// <summary>
    /// Builds the message hub from the configured settings.
    /// </summary>
    /// <param name="sp">The service provider to use for building the hub.</param>
    /// <returns>The configured message hub.</returns>
    public virtual IMessageHub BuildHub(IServiceProvider sp)
    {
        return sp.CreateMessageHub(Address, conf => HubConfiguration.Aggregate(conf, (x, y) => y.Invoke(x)));
    }
    private static MessageHubConfiguration AddMesh(MessageHubConfiguration configuration)
    {
        return configuration
            .AddMeshTypes()
            .WithNodeOperationHandlers();
    }

    /// <summary>
    /// Adds mesh nodes to the mesh configuration.
    /// </summary>
    /// <param name="nodes">The mesh nodes to add.</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder AddMeshNodes(params IEnumerable<MeshNode> nodes)
    {
        MeshNodes.AddRange(nodes);
        return this;
    }

    /// <summary>
    /// Adds each node whose <see cref="MeshNode.Path"/> is not already seeded on this builder.
    ///
    /// <para>For PARTITION-LEVEL GOVERNANCE that several independent modules must each be able to
    /// guarantee on their own. The motivating case is the <c>Templates</c> partition's access
    /// grant (<see cref="ScriptTemplates.PublicExecuteGrant"/>): <c>AddGraph()</c> seeds
    /// <c>Templates/Import/*</c> and <c>AddMarkdownExport()</c> seeds <c>Templates/Export/*</c>,
    /// and either call ALONE must land the grant while both together must land it ONCE. Plain
    /// <see cref="AddMeshNodes"/> appends unconditionally, so the two would seed a duplicate.</para>
    ///
    /// <para>State lives on this builder instance only — no static registry, nothing process-wide
    /// (AGENTS.md → "No static collections").</para>
    /// </summary>
    /// <param name="nodes">The mesh nodes to add if their path is not already present.</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder AddMeshNodesIfAbsent(params IEnumerable<MeshNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (MeshNodes.Any(existing =>
                    string.Equals(existing.Path, node.Path, StringComparison.OrdinalIgnoreCase)))
                continue;
            MeshNodes.Add(node);
        }
        return this;
    }

    /// <summary>
    /// Registers a type on the mesh-level TypeRegistry for cross-hub serialization.
    /// Use this to register content types that need to be serialized across hub boundaries.
    /// </summary>
    /// <typeparam name="T">The type to register.</typeparam>
    /// <param name="name">The short name for the type (defaults to type name).</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder WithMeshType<T>(string? name = null)
    {
        MeshTypeRegistrations.Add((typeof(T), name ?? typeof(T).Name));
        return this;
    }

    /// <summary>
    /// Registers a type on the mesh-level TypeRegistry for cross-hub serialization.
    /// Use this to register content types that need to be serialized across hub boundaries.
    /// </summary>
    /// <param name="type">The type to register.</param>
    /// <param name="name">The short name for the type (defaults to type name).</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder WithMeshType(Type type, string? name = null)
    {
        MeshTypeRegistrations.Add((type, name ?? type.Name));
        return this;
    }

    private List<(Type Type, string Name)> MeshTypeRegistrations { get; } = new();

    /// <summary>
    /// Adds node types to be excluded from autocomplete/search results.
    /// Use this for satellite types (Comment, Thread) and internal types (AccessAssignment).
    /// </summary>
    public MeshBuilder AddAutocompleteExcludedTypes(params string[] nodeTypes)
    {
        foreach (var t in nodeTypes)
            AutocompleteExcludedTypes.Add(t);
        return this;
    }

    private HashSet<string> AutocompleteExcludedTypes { get; } = new();

    /// <summary>
    /// Configures node type access permissions (e.g., public-read types).
    /// </summary>
    public MeshBuilder ConfigureNodeTypeAccess(Action<NodeTypeAccessBuilder> configure)
    {
        configure(NodeTypeAccessConfig);
        return this;
    }

    internal NodeTypeAccessBuilder NodeTypeAccessConfig { get; } = new();

    /// <summary>
    /// Registers a query routing rule that resolves partition and/or table hints from a ParsedQuery.
    /// Rules are applied in order during query execution; first non-null Partition/Table wins.
    /// Use this to restrict fan-out queries (e.g., nodeType:User → partition "User").
    /// </summary>
    public MeshBuilder AddQueryRoutingRule(QueryRoutingRule rule)
    {
        QueryRoutingRules.Add(rule);
        return this;
    }

    internal List<QueryRoutingRule> QueryRoutingRules { get; } = [];

    /// <summary>
    /// Declares an address-type prefix that routes via the cluster-wide
    /// Orleans memory stream rather than grain activation. Hubs at such
    /// addresses are expected to <see cref="IRoutingService.RegisterStream(IMessageHub)"/>
    /// in their <c>WithInitialization</c>. Built-in defaults
    /// (<c>portal</c>, <c>client</c>) come from
    /// <see cref="MeshConfiguration.DefaultStreamRoutedAddressTypes"/>;
    /// modules add their own (e.g. <c>cache</c> for the mesh-node-cache
    /// hub) here. See <c>Doc/Architecture/OrleansTestRoutingPattern.md</c>.
    /// </summary>
    public MeshBuilder AddStreamRoutedAddressType(string addressType)
    {
        StreamRoutedAddressTypes.Add(addressType);
        return this;
    }

    internal HashSet<string> StreamRoutedAddressTypes { get; } =
        new(global::MeshWeaver.Mesh.MeshConfiguration.DefaultStreamRoutedAddressTypes, StringComparer.Ordinal);

    /// <summary>
    /// Declares that hubs at this address-type prefix are hosted in an Orleans CLIENT process,
    /// which cannot host a grain. For those — and ONLY those — the Orleans memory stream stays the
    /// transport when the pod-hub grain answers "not here": there is no directed call that could
    /// ever reach them. See <see cref="MeshConfiguration.ClientHostedAddressTypes"/> for why this
    /// is a declaration rather than an inference from the grain's answer, and
    /// <c>Doc/Architecture/DurableStreamsViaMeshNodes</c> for the design.
    ///
    /// <para>🚨 <b>Nothing in production declares one.</b> This exists for the Orleans test rig,
    /// which hosts hubs on a cluster client. Declaring a type here opts it
    /// OUT of the transient NACK and back into a stream publish that succeeds-and-discards when
    /// nobody is subscribed — do not add one to make a routing symptom go away.</para>
    /// </summary>
    /// <param name="addressType">The address-type prefix (e.g. <c>client</c>).</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder AddClientHostedAddressType(string addressType)
    {
        ClientHostedAddressTypes.Add(addressType);
        return this;
    }

    internal HashSet<string> ClientHostedAddressTypes { get; } = new(StringComparer.Ordinal);
}
