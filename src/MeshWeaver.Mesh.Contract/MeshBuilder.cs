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
    /// Installs mesh nodes from the specified assembly locations — each path is one generation
    /// with no fallback, which is every baseline entry and every caller that installs a module by
    /// hand. The boot path that also knows a module's PREVIOUS generation calls
    /// <see cref="InstallModules"/> instead.
    /// </summary>
    /// <param name="assemblyLocations">Paths to assemblies containing MeshNodeProviderAttribute definitions.</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder InstallAssemblies(params string[] assemblyLocations) =>
        InstallModules([.. assemblyLocations.Select(location => new ModuleInstallCandidate(location))]);

    /// <summary>
    /// Installs mesh nodes from the given module candidates — the newest generation of each, or,
    /// when that one cannot load on this platform and the candidate names a previous generation,
    /// the previous one (#3649).
    ///
    /// <para>🚨 <b>A distinct name, deliberately not an overload of <see cref="InstallAssemblies"/>.</b>
    /// Dependent repositories carry <c>&lt;see cref="MeshBuilder.InstallAssemblies"/&gt;</c> in
    /// their own doc comments; an added overload turns every one of those into a CS0419 under
    /// <c>-warnaserror</c>, on pull requests that did not make the change (see
    /// <see cref="IncompatibleModule.FromLinkRefusal"/> for the same rule). The string form stays
    /// byte-for-byte what it was and forwards here.</para>
    ///
    /// <para><b>The fallback rule.</b> A candidate's <see cref="ModuleInstallCandidate.Location"/>
    /// is measured by the link probe and then loaded. If it is refused BEFORE loading, or
    /// <c>Assembly.LoadFrom</c> itself throws, and the candidate resolves a
    /// <see cref="ModuleInstallCandidate.Previous"/> generation, that generation goes through the
    /// same probe and load; when it succeeds the module is installed from it and recorded as a
    /// <see cref="FallbackModule"/> — present, running, and behind — with one
    /// <c>[MeshWeaver.Mesh.FallbackModule]</c> line on stderr naming both generations and why.
    /// When that one does not load either — or the candidate holds none — and the candidate names
    /// an <see cref="ModuleInstallCandidate.ImageBaseline"/>, the IMAGE-SHIPPED copy of the module
    /// goes through the same probe and load and is recorded as a <see cref="FallbackModule"/>
    /// with <see cref="FallbackModule.RunsImageBaseline"/> (#3735). Only when nothing loads is
    /// the module an <see cref="IncompatibleModule"/>, exactly as before — with the reasons of
    /// every step it tried on the record. 🚨 A generation whose assembly LOADED and whose
    /// registration then threw (#2234's shape) is never swapped for the previous one or the image
    /// copy: two assemblies of one simple name cannot coexist in the default load context, so the
    /// fallback exists only for a generation that never made it into the process — the link
    /// probe's case, the whole of #3649 and #3735. That shape is named on stderr when an image
    /// copy exists, so nobody reads the absent module as "the image had nothing".</para>
    /// </summary>
    /// <param name="modules">The candidates, in install order.</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder InstallModules(IReadOnlyList<ModuleInstallCandidate> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
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
        var fallbacks = new List<FallbackModule>();
        // 🚨 #3538 — THE LINK PROBE, before a single Assembly.LoadFrom. Per-module isolation above
        // catches a module that THROWS while installing; it cannot catch one that installs cleanly
        // and is linked against types this platform does not have, because nothing touches those
        // types until a render does. memex-cloud adopted a MeshWeaver.Graph.Views built against a
        // core three days newer than its own, installed it without a murmur, and then threw
        // `TypeLoadException: Could not load type 'MeshWeaver.Mesh.CodeOutputCurrency'` on EVERY
        // code cell for every user until a human read a pod log. The requirement was never a
        // version string — it is the set of types the bytes are linked against, and that is in the
        // module's own metadata, so it can be MEASURED here instead of taken on trust.
        //
        // One surface per call, so 40 modules read each platform assembly's type list once. A
        // baseline (image) module sits IN the application closure, so every platform assembly is
        // its own sibling and it checks trivially clean — which is right, it ships with the
        // platform by construction.
        //
        // 🚨 The surface is the application closure PLUS every directory this batch is loading
        // from — never the app closure alone. A store-landed module lives in its own generation
        // directory and may legitimately reference ANOTHER module landed beside it; measuring it
        // against /app alone would report that sibling as an absent platform assembly and
        // quarantine a module that is perfectly fine. The runtime's resolution surface is what has
        // to be measured, and at boot that is exactly these directories.
        var probeDirectories = modules
            .Select(module => Path.GetDirectoryName(Path.GetFullPath(module.Location)))
            .Where(directory => !string.IsNullOrEmpty(directory))
            .Select(directory => directory!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var surface = modules.Count > 0
            ? ModulePlatformSurface.OfRunningProcess([AppContext.BaseDirectory, .. probeDirectories])
            : null;
        foreach (var module in modules)
        {
            var newest = TryLoad(module.Location, surface);
            if (newest.Loaded is not null)
            {
                pending.Add(newest.Loaded);
                // 🚨 The generation loaded — but not necessarily THIS one. When the default load
                // context already held this identity, LoadFrom silently handed back that copy, and
                // what runs is the generation at LoadedFrom. Recorded as a FallbackModule for the
                // same reason the link-refusal fallbacks are: the module is present and running,
                // one generation is in effect and another is not, and a surface that cannot say
                // which reports a restart no restart can clear.
                if (newest.LoadedFrom is { } already)
                    fallbacks.Add(ReportFallback(new FallbackModule(
                        newest.Loaded.Assembly.GetName().Name ?? Path.GetFileNameWithoutExtension(module.Location),
                        module.Location, already,
                        "this process had already loaded an assembly of that name, and the default "
                        + "load context holds one copy per name")
                    {
                        Version = module.Version,
                        RunsAlreadyLoadedCopy = true,
                    }));
                continue;
            }

            // 🚨 #3649 — the newest generation never made it into the process. Before this the
            // module was simply ABSENT from here on (unless the image shipped a baseline copy),
            // and the generation that DID load last time was unreferenced and reclaimed by the
            // next GC pass. Rule R1: an installation runs the newest generation that LOADS, and
            // keeps the one it has until a newer one does.
            //
            // 🚨 #3735 — and the fallback ORDER has a third step. "Unless the image shipped a
            // baseline copy" above was never true: the boot union substitutes the landed entry IN
            // PLACE of the same-named Modules:Assemblies entry before anything is measured, so a
            // refused store generation SHADOWED the image copy that loads by construction. On
            // memex.systemorph.com (2026-09-08) a two-week-old store generation of
            // MeshWeaver.Blazor.Views was refused for a type the platform had since removed, the
            // image's own copy was never tried, and every skinned control rendered its fallback
            // HTML — a client-visible outage caused by a module set that "contributed nothing"
            // over a baseline that would have worked. The order is: newest → the previous landed
            // generation → the image-shipped copy → absent (reported), and every step is
            // MEASURED by the same probe and load as the one before it.
            var reason = newest.Refused!.Error;
            var previous = newest.NeverLoaded ? ResolvePrevious(module) : null;
            if (previous is not null)
            {
                // The previous generation lives in its own directory, which the surface above
                // does not carry: a fresh one for the retry, so a sibling module it references is
                // measured as present rather than as an absent platform assembly.
                var retry = TryLoad(previous, SurfaceIncluding(probeDirectories, previous));
                if (retry.Loaded is not null)
                {
                    pending.Add(retry.Loaded);
                    // The row names the file that is RUNNING, which is the previous generation
                    // unless the load context already held this identity elsewhere — then it is
                    // that copy, and saying "previous" would name a generation nothing is running.
                    fallbacks.Add(ReportFallback(new FallbackModule(
                        newest.Refused.Name, module.Location, retry.LoadedFrom ?? previous, reason)
                    {
                        Version = module.Version,
                        PreviousVersion = retry.LoadedFrom is null ? module.PreviousVersion : null,
                    }));
                    continue;
                }

                Console.Error.WriteLine(
                    $"[MeshWeaver.Mesh.FallbackModule] '{newest.Refused.Name}': the previous "
                    + $"generation '{previous}' cannot load here either ({retry.Refused!.Error}).");
                reason += $"; the previous generation '{Path.GetFileName(Path.GetDirectoryName(previous))}' "
                    + $"cannot load here either: {retry.Refused.Error}";
                // A previous generation that LOADED and then failed to materialise holds the
                // simple name now; nothing else of that name can enter the process (see
                // LoadAttempt.NeverLoaded), so the image copy is out of reach exactly like it
                // would be after the newest one loaded.
                if (!retry.NeverLoaded)
                {
                    ReportBaselineOutOfReach(module, newest.Refused.Name);
                    incompatible.Add(Report(newest.Refused with { Error = reason }));
                    continue;
                }
            }

            if (newest.NeverLoaded && ResolveImageBaseline(module) is { } baseline)
            {
                var image = TryLoad(baseline, SurfaceIncluding(probeDirectories, baseline));
                if (image.Loaded is not null)
                {
                    pending.Add(image.Loaded);
                    fallbacks.Add(ReportFallback(new FallbackModule(
                        newest.Refused.Name, module.Location, image.LoadedFrom ?? baseline, reason)
                    {
                        Version = module.Version,
                        // RunsImageBaseline only when the image copy is what actually loaded: a
                        // substituted load runs neither the head nor the image, and stamping the
                        // '@image' generation on it would record a generation nothing is running.
                        //
                        // 🚨 And NOT RunsAlreadyLoadedCopy, whatever was substituted here. That
                        // flag means "the HEAD was never measured", which is false on this branch:
                        // the head was measured and refused, and the unloadable marker it earns
                        // must still be written. The substitution here decides only WHICH copy the
                        // row names as running.
                        RunsImageBaseline = image.LoadedFrom is null,
                    }));
                    continue;
                }

                // An image copy that does not load is a build defect of the image itself, which is
                // a different fault from the store generation's — said separately, and the module
                // is absent as before.
                Console.Error.WriteLine(
                    $"[MeshWeaver.Mesh.FallbackModule] '{newest.Refused.Name}': the image-shipped "
                    + $"baseline '{baseline}' cannot load here either ({image.Refused!.Error}) — "
                    + "no generation of this module loads on this platform, so it is absent.");
                reason += $"; the image-shipped baseline cannot load here either: {image.Refused.Error}";
            }
            else if (!newest.NeverLoaded)
            {
                ReportBaselineOutOfReach(module, newest.Refused.Name);
            }
            else
            {
                Console.Error.WriteLine(
                    $"[MeshWeaver.Mesh.FallbackModule] '{newest.Refused.Name}': no previous "
                    + "generation and no image-shipped baseline loads here — no generation of this "
                    + "module loads on this platform, so it is absent.");
            }

            incompatible.Add(Report(newest.Refused with { Error = reason }));
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
        if (incompatible.Count > 0 || fallbacks.Count > 0)
        {
            result.ConfigureServices(services =>
            {
                foreach (var module in incompatible)
                    services.AddSingleton(module);
                // A fallback is registered as its own record, never as an IncompatibleModule: it
                // is running, and the surfaces that read the incompatible set (the readiness
                // probe, the package card's "not running here") must not read it as degraded.
                foreach (var module in fallbacks)
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
    /// The outcome of probing and loading ONE generation: what was materialised, or why not.
    /// </summary>
    /// <param name="Loaded">The module's contributions, when the generation loaded and
    /// materialised cleanly.</param>
    /// <param name="Refused">The record of the failure, otherwise.</param>
    /// <param name="NeverLoaded">True when the generation's assembly never entered the process —
    /// refused by the link probe, or <c>Assembly.LoadFrom</c> threw — which is the ONLY state a
    /// previous generation can be tried from: an assembly that loaded and then failed to
    /// materialise holds its simple name in the default load context, and a second assembly of
    /// that name cannot be loaded beside it.</param>
    /// <param name="LoadedFrom">The file the loaded assembly ACTUALLY came from, when it is not
    /// the one <see cref="TryLoad"/> was asked for (<see cref="SubstitutedLocationOf"/>); null
    /// when the load did what it was asked, and always null when nothing loaded.</param>
    private sealed record LoadAttempt(
        PendingModuleInstall? Loaded, IncompatibleModule? Refused, bool NeverLoaded,
        string? LoadedFrom = null);

    /// <summary>
    /// 🚨 <b>The file <paramref name="loaded"/> actually came from, when it is NOT the one that was
    /// asked for — otherwise <c>null</c>. <c>Assembly.LoadFrom</c> does not promise to load the
    /// path it is given, and this is the only thing that can tell.</b>
    ///
    /// <para><b>Measured, 2026-09-10.</b> <c>Assembly.LoadFrom</c> against a copy of an assembly the
    /// default load context already holds returns THAT copy — same instance, its own
    /// <see cref="Assembly.Location"/>, no exception, no diagnostic. (Only a copy carrying
    /// DIFFERENT bytes throws <c>FileLoadException: Assembly with same name is already loaded</c>,
    /// which the loader already handles as "never loaded" and falls back from.) So the silent
    /// branch is exactly the one nothing was watching: a module the image ships as a
    /// <c>MeshModuleClosure</c> SEED under <c>modules/&lt;name&gt;/</c> and the registry lands again
    /// under <c>modules/&lt;name&gt;@&lt;generation&gt;/</c> binds whichever path was reached first,
    /// and every later reading of "which generation is this process running" —
    /// <see cref="InstalledModuleAssembly"/>, <c>ModuleActivationStatus.LoadedModuleGenerations</c>,
    /// the per-NodeType dependency record, the module-set adoption — inherits the answer without
    /// anyone comparing it to the one that was requested.</para>
    ///
    /// <para>🚨 <b>Why that silence is the defect and not the substitution.</b> The activation
    /// record names the generation it landed; the process runs another; the derivation reads the
    /// difference as an ordinary pending update and prints <i>restart to activate</i> — a prompt no
    /// restart can clear, because the next boot resolves the same two paths the same way. Naming it
    /// turns an invisible, self-renewing state into a <see cref="FallbackModule"/>: present,
    /// running, and behind, which every status surface already knows how to say.</para>
    ///
    /// <para>🚨 <b>The comparison is over the GENERATION, not the path</b> — the containing
    /// directory's leaf, the same identity <see cref="FallbackModule.Generation"/> and
    /// <c>ModuleActivationStatus.LoadedModuleGenerations</c> use, so all three agree by
    /// construction. A different PATH with the same leaf is the same generation reached twice, and
    /// that is routine rather than a finding: every boot copies the generation WITH its leaf into
    /// fresh process-local storage before loading (<c>ModuleGenerationPin</c>, #2509), so a second
    /// boot in one process legitimately asks for a new path holding the generation already running.
    /// Reporting that would put a row on every status surface saying a module runs the generation
    /// it runs — noise, and an operator who learns to scroll past this line has lost the signal it
    /// exists to carry. Case-INSENSITIVE, because a generation is <c>&lt;name&gt;@&lt;id&gt;</c> or
    /// the seed's <c>&lt;name&gt;</c> and never two leaves differing only in case, while comparing
    /// case-sensitively would invent a finding on a case-insensitive filesystem.</para>
    ///
    /// <para>An assembly with no readable location (loaded from bytes) answers <c>null</c>: "cannot
    /// see" is never reported as a finding.</para>
    /// </summary>
    /// <param name="loaded">The assembly <c>Assembly.LoadFrom</c> returned.</param>
    /// <param name="requestedLocation">The path it was asked to load.</param>
    internal static string? SubstitutedLocationOf(Assembly loaded, string requestedLocation)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (string.IsNullOrWhiteSpace(requestedLocation))
            return null;
        string actual;
        try
        {
            actual = loaded.Location;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        if (string.IsNullOrEmpty(actual))
            return null;
        try
        {
            var actualGeneration = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(actual)));
            var requestedGeneration =
                Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(requestedLocation)));
            // An unreadable leaf on either side is "cannot see", not "they differ".
            if (string.IsNullOrEmpty(actualGeneration) || string.IsNullOrEmpty(requestedGeneration))
                return null;
            return string.Equals(actualGeneration, requestedGeneration, StringComparison.OrdinalIgnoreCase)
                ? null
                : actual;
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException
                                              or NotSupportedException or IOException
                                              or System.Security.SecurityException)
        {
            // An unnormalisable path is "cannot see", never a finding — the same discipline
            // ModuleLoadReport keeps for an unreadable DLL.
            return null;
        }
    }

    /// <summary>
    /// Where the simple name <paramref name="simpleName"/> is already held in this process, for the
    /// refusal message when <c>Assembly.LoadFrom</c> answers <i>Assembly with same name is already
    /// loaded</i>. That exception names the identity and never the holder, so on its own it tells an
    /// operator nothing they can act on. Null when nothing of that name is loaded, or when the
    /// holder has no readable location.
    /// </summary>
    private static string? WhereTheNameIsAlreadyHeld(string simpleName)
    {
        if (string.IsNullOrEmpty(simpleName))
            return null;
        foreach (var candidate in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (candidate.IsDynamic
                || !string.Equals(candidate.GetName().Name, simpleName, StringComparison.Ordinal))
                continue;
            try
            {
                return string.IsNullOrEmpty(candidate.Location) ? null : candidate.Location;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Probes one generation against <paramref name="surface"/> and loads it. Records a failure
    /// without reporting it — the caller decides whether the failure is a fault (nothing else
    /// loads) or a fallback (the previous generation does), and the two are reported differently.
    /// </summary>
    private static LoadAttempt TryLoad(string location, ModulePlatformSurface? surface)
    {
        // Fail CLOSED on Indeterminate: MayLoad is true for Linkable and nothing else, so a
        // check that could not be made can never be read as a check that passed.
        if (surface is not null && ModulePlatformLink.Check(location, surface) is { MayLoad: false } verdict)
            return new LoadAttempt(null, IncompatibleModule.FromLinkRefusal(location, verdict), NeverLoaded: true);

        Assembly assembly;
        try
        {
            assembly = Assembly.LoadFrom(location);
        }
        catch (Exception exception)
        {
            // 🚨 The name-is-taken refusal ("Assembly with same name is already loaded") names the
            // identity and never the holder, so on its own it cannot be acted on. Say WHERE the
            // name is held — that path is the generation this process is actually running.
            var refusal = IncompatibleModule.From(location, exception);
            var holder = exception is FileLoadException
                ? WhereTheNameIsAlreadyHeld(refusal.Name)
                : null;
            return new LoadAttempt(
                null,
                holder is null
                    ? refusal
                    : refusal with
                    {
                        Error = refusal.Error
                            + $" — that name is already held in this process by '{holder}', "
                            + "which is the generation running here.",
                    },
                NeverLoaded: true);
        }

        // 🚨 LoadFrom does not promise to load the path it was given: an assembly of this identity
        // already in the default load context is returned instead, silently. Measure it here — the
        // one place that knows both what was asked for and what arrived — so nothing downstream has
        // to take the requested path on trust. See SubstitutedLocationOf.
        var substituted = SubstitutedLocationOf(assembly, location);

        try
        {
            var moduleAttributes = assembly.GetCustomAttributes<MeshNodeProviderAttribute>().ToArray();
            return new LoadAttempt(
                new PendingModuleInstall(
                    assembly,
                    moduleAttributes.SelectMany(a => a.Nodes).ToArray(),
                    moduleAttributes.SelectMany(a => a.AddressTypes).ToArray(),
                    moduleAttributes.SelectMany(a => a.HubConfigurations).ToArray(),
                    moduleAttributes.SelectMany(a => a.DefaultNodeHubConfigurations).ToArray(),
                    moduleAttributes.SelectMany(a => a.BuilderConfigurations).ToArray()),
                null,
                NeverLoaded: false,
                LoadedFrom: substituted);
        }
        catch (Exception exception)
        {
            return new LoadAttempt(null, IncompatibleModule.From(location, exception), NeverLoaded: false);
        }
    }

    /// <summary>
    /// The previous generation's entry DLL for a candidate whose newest generation never loaded,
    /// or null when there is none to try. A resolver that throws is "none", said on stderr: the
    /// fallback is protection for the module, never a new way to take the boot down.
    /// </summary>
    private static string? ResolvePrevious(ModuleInstallCandidate module)
    {
        if (module.Previous is null)
            return null;
        try
        {
            var previous = module.Previous();
            return string.IsNullOrWhiteSpace(previous) || !File.Exists(previous) ? null : previous;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[MeshWeaver.Mesh.FallbackModule] '{Path.GetFileNameWithoutExtension(module.Location)}': "
                + $"resolving the previous generation threw ({exception.GetType().Name}: "
                + $"{exception.Message}) — no fallback is attempted.");
            return null;
        }
    }

    /// <summary>
    /// The image-shipped copy of a candidate's module, when the image ships one AND it is on disk
    /// — the last step of the fallback order (#3735). Null otherwise. A candidate that names a
    /// baseline which is not there is the listed-but-absent shape the boot already skips loudly
    /// for baseline entries; here it simply means there is nothing to fall back to.
    /// </summary>
    private static string? ResolveImageBaseline(ModuleInstallCandidate module) =>
        !string.IsNullOrWhiteSpace(module.ImageBaseline) && File.Exists(module.ImageBaseline)
            ? module.ImageBaseline
            : null;

    /// <summary>
    /// The link-probe surface for a fallback attempt: the application closure, every directory
    /// this batch loads from, and the directory of <paramref name="candidate"/> itself — which the
    /// batch surface does not carry, so a sibling module the candidate references is measured as
    /// present rather than as an absent platform assembly.
    /// </summary>
    private static ModulePlatformSurface SurfaceIncluding(string[] probeDirectories, string candidate)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(candidate));
        return ModulePlatformSurface.OfRunningProcess([
            AppContext.BaseDirectory,
            .. probeDirectories,
            .. string.IsNullOrEmpty(directory) ? [] : new[] { directory },
        ]);
    }

    /// <summary>
    /// Writes a fallback to stderr and hands it back for registration — once per boot, because
    /// the logging pipeline does not exist yet (see <see cref="ReportIncompatible"/>). The portal
    /// re-logs it as a Warning once it does.
    /// </summary>
    private static FallbackModule ReportFallback(FallbackModule fallback)
    {
        Console.Error.WriteLine($"[MeshWeaver.Mesh.FallbackModule] {fallback.Report()}");
        return fallback;
    }

    /// <summary>
    /// Says why the image-shipped copy could NOT take over for a module whose generation LOADED
    /// and then failed to install (#2234's shape): the default load context holds one assembly
    /// per simple name, so once the store generation is in, the image's copy of the same name
    /// cannot be loaded beside it. Silent otherwise — a candidate with no image baseline has
    /// nothing to say here. Not a band-aid on the constraint, a NAME for it: the operator reads
    /// "the image copy would have worked, and this is why it did not run" instead of an
    /// incompatible module whose baseline is simply not mentioned.
    /// </summary>
    private static void ReportBaselineOutOfReach(ModuleInstallCandidate module, string name)
    {
        if (string.IsNullOrWhiteSpace(module.ImageBaseline))
            return;
        Console.Error.WriteLine(
            $"[MeshWeaver.Mesh.FallbackModule] '{name}': the image ships a baseline copy "
            + $"('{module.ImageBaseline}'), but the landed generation LOADED before it failed to "
            + "install, and the default load context holds one assembly per simple name — the "
            + "image copy cannot be loaded beside it, so the module is absent until the next "
            + "restart measures a generation that does not load at all, or one that installs.");
    }

    /// <summary>
    /// Writes a module that could not install to stderr and hands it back for registration.
    ///
    /// <para>🚨 stderr, not a logger: this runs BEFORE the logging pipeline exists, which is
    /// exactly why #2234's crash left a container log containing only the createdump DSO listing
    /// and cost most of a day to diagnose. The one channel that works at this point is the one the
    /// container captures.</para>
    /// </summary>
    private static IncompatibleModule Report(IncompatibleModule module)
    {
        Console.Error.WriteLine($"[MeshWeaver.Mesh.IncompatibleModule] {module.Report()}");
        return module;
    }

    /// <summary>
    /// Records a module that could not install, and writes it to stderr — see
    /// <see cref="Report(IncompatibleModule)"/>.
    /// </summary>
    private static IncompatibleModule ReportIncompatible(string entry, Exception exception) =>
        Report(IncompatibleModule.From(entry, exception));

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
            // The mesh's INodeTypeAccessRule index — the ONE answer to "does a rule govern this
            // (node type, operation)?", shared by RlsNodeValidator and the delete handler's
            // pre-flight so the two cannot disagree again (#2913). Registered at the ROOT for the
            // same reason the tombstone registry above is: the delete handler resolves it off a hub
            // ServiceProvider that chains here, while the validator resolves it from its own scope,
            // and every access rule in the fleet is registered on the MESH service collection.
            .AddSingleton<Services.NodeTypeAccessRuleSet>()
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
            // Which package roots an install is writing under RIGHT NOW (#3510). Registered at the
            // ROOT for the same reason as the three registries above: the holder is
            // PackageInstaller, running on the mesh hub's own chain, while the readers are
            // recyclers living on per-node hubs — NodeTypeRebindWatcher arms on every instance hub
            // at activation, and HubRecycleExtensions runs on whatever surviving hub its caller
            // holds. A hub-level registration would give each of them its own instance, i.e. a
            // lease nobody else can see, which is the same defect as a guard that checks a registry
            // no writer ever opened a scope on.
            .AddSingleton<Services.PackageRootInstallLeases>()
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
