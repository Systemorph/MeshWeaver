using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// One loaded generation of one module: the collectible context it runs in and its entry
/// assembly. The unit <see cref="ModuleContexts"/> installs, swaps and retires.
/// </summary>
/// <param name="Name">The module's entry-assembly simple name.</param>
/// <param name="Location">The entry DLL this generation was loaded from.</param>
/// <param name="Context">The collectible context it runs in.</param>
/// <param name="Assembly">The module's entry assembly, loaded in <paramref name="Context"/>.</param>
public sealed record ModuleGeneration(string Name, string Location, ModuleLoadContext Context, Assembly Assembly)
{
    /// <summary>What this generation contributes, once materialised; null while it is being loaded.</summary>
    public ModuleContributions? Contributions { get; internal set; }

    /// <summary>This generation's root services, served from a scope of its own (see
    /// <see cref="ModuleServices"/>); null when it registers none, or when they could not be
    /// converted and went straight into the root (then the module is restart-required).</summary>
    public ModuleServices? Services { get; internal set; }

    /// <summary>Why this generation's root services went straight into the root container instead of
    /// a scope of its own — empty when they did not.</summary>
    public System.Collections.Immutable.ImmutableList<string> RootServiceBlockers { get; internal set; } = [];
}

/// <summary>
/// An assembly <see cref="ModuleContexts.ResolveDependency"/> bound for a context outside the
/// modules, the module context it lives in, and a lease on that context — taken BEFORE anything was
/// loaded from it, while it was still current, so it cannot have started unloading. The caller holds
/// <paramref name="Lease"/> for as long as it can call into the assembly, and disposes it after.
/// </summary>
/// <param name="Assembly">The bound assembly.</param>
/// <param name="Context">The module context that holds it.</param>
/// <param name="Lease">The caller's lease on <paramref name="Context"/> (<see cref="ModuleContexts.Leases"/>).</param>
public sealed record ModuleBinding(Assembly Assembly, ModuleLoadContext Context, IDisposable Lease);

/// <summary>
/// The mesh's modules, each in its OWN collectible load context — the registry a live module
/// update swaps generations in (policy <c>module-live-update-default</c>,
/// <c>Doc/Architecture/LiveModuleUpdate</c>).
///
/// <para><b>What it owns.</b> For every module name, the CURRENT generation. A generation is
/// loaded (<see cref="Load"/>), its contributions are materialised by the caller, and only then is
/// it made current (<see cref="Commit"/>) — so a generation that fails to materialise never becomes
/// the one other contexts resolve, and the one that was serving keeps serving. A generation that
/// stops being current is RETIRED (<see cref="Retire"/>): unloaded on a POSITIVE quiescence signal
/// from <see cref="Leases"/> and recorded on the mesh's <see cref="CollectibleContextUnloads"/> so
/// "it has really been collected" is observable — never on a timer (the use-after-unload SIGSEGV,
/// <see cref="AlcLeaseRegistry"/>).</para>
///
/// <para><b>What stays in the default context.</b> A module whose entry assembly is part of the
/// application's own closure (<see cref="IsImageBound"/> — it is in
/// <c>TRUSTED_PLATFORM_ASSEMBLIES</c>) is BOUND to the image: the default context resolves that
/// name for every platform assembly that references it, so a second copy in a module context would
/// split the identity. Those keep loading exactly as before and are what a live update cannot
/// reach; the registry does not hold them.</para>
///
/// <para>Mesh-scoped instance state (NoStaticState): created by the <see cref="MeshBuilder"/> that
/// installs the modules, registered as that mesh's singleton, and disposed with it — which retires
/// every generation it still holds.</para>
/// </summary>
public sealed class ModuleContexts : IDisposable, MeshWeaver.Layout.Client.IViewContributionSource
{
    private readonly ConcurrentDictionary<string, ModuleGeneration> current = new(StringComparer.Ordinal);
    // Which module contributed a mesh node — by REFERENCE, weakly: the seed node list asks it so a
    // module's nodes can be served from its CURRENT generation once the generation that contributed
    // them at boot has been swapped out (see StaticMeshNodeListProvider).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshNode, string> nodeOwners = new();
    private long sequence;
    private CollectibleContextUnloads? unloads;
    private ILogger? logger;
    private IServiceProvider? root;
    private int disposed;

    private long version;
    private readonly System.Reactive.Subjects.Subject<long> versionChanged = new();

    /// <summary>
    /// Emits the new <see cref="Version"/> whenever which generation is current changes. A cache of a
    /// module's contributions DROPS what it holds here — waiting for its next read to notice would keep
    /// the swapped-out generation referenced, and so loaded, until something happened to ask
    /// (measured: the static-node query catalog pinned a retired generation exactly that way).
    /// </summary>
    public IObservable<long> VersionChanged => versionChanged;

    private void Bump()
    {
        var now = Interlocked.Increment(ref version);
        versionChanged.OnNext(now);
    }

    /// <summary>The view registrations of every module's CURRENT generation, ordered by module name —
    /// what the layout client re-reads when <see cref="Version"/> moves.</summary>
    public IReadOnlyList<Func<MeshWeaver.Layout.Client.LayoutClientConfiguration, MeshWeaver.Layout.Client.LayoutClientConfiguration>> ViewConfigurations =>
        current.Values.OrderBy(g => g.Name, StringComparer.Ordinal)
            .SelectMany(g => g.Contributions?.Views ?? [])
            .ToArray();

    /// <summary>Moves on every change of which generation is current — what a cached view of the
    /// modules' contributions (the static-node query catalog) compares against.</summary>
    public long Version => Interlocked.Read(ref version);

    /// <summary>
    /// Makes a SLOT — a boot-time stand-in for <paramref name="node"/>, one of <paramref name="moduleName"/>'s
    /// nodes, stripped of everything that can reference module code — as owned by that module, so the
    /// static-node list serves the module's CURRENT nodes in its place and never pins a generation.
    /// </summary>
    public MeshNode SlotFor(MeshNode node, string moduleName)
    {
        ArgumentNullException.ThrowIfNull(node);
        var slot = node with
        {
            Content = null,
            HubConfiguration = null,
            GlobalServiceConfigurations = [],
            ExcludeFromContext = node.ExcludeFromContext?.ToArray(),
        };
        nodeOwners.AddOrUpdate(slot, moduleName);
        return slot;
    }

    /// <summary>Who is executing inside which module generation — the quiescence an unload waits on.</summary>
    public AlcLeaseRegistry Leases { get; } = new();

    /// <summary>The current generation of every module this registry holds.</summary>
    public IReadOnlyCollection<ModuleGeneration> Generations => current.Values.ToArray();

    /// <summary>The current generation of <paramref name="moduleName"/>, or null.</summary>
    public ModuleGeneration? Current(string moduleName) =>
        current.TryGetValue(moduleName, out var generation) ? generation : null;

    /// <summary>
    /// Whether <paramref name="simpleName"/> is part of the application's own closure — listed in
    /// <c>TRUSTED_PLATFORM_ASSEMBLIES</c>, so the default context binds it for every platform
    /// assembly that references it. Such a module cannot live in its own context without splitting
    /// its identity, so it is loaded into the default context and its update needs a restart.
    /// Pure over the process's TPA list.
    /// </summary>
    public static bool IsImageBound(string simpleName)
    {
        if (string.IsNullOrEmpty(simpleName)
            || AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpa)
            return false;
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(Path.GetFileNameWithoutExtension(path), simpleName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Attaches the mesh's collection tracker and logger once the container exists. Retirements
    /// before this are still unloaded; they are just not observable as collected.
    /// </summary>
    public ModuleContexts Attach(CollectibleContextUnloads? collectibleUnloads, ILogger? log)
        => Attach(collectibleUnloads, log, null);

    /// <summary>As <see cref="Attach(CollectibleContextUnloads?, ILogger?)"/>, plus the ROOT provider
    /// a module's service scope is a child of.</summary>
    public ModuleContexts Attach(CollectibleContextUnloads? collectibleUnloads, ILogger? log, IServiceProvider? rootProvider)
    {
        unloads ??= collectibleUnloads;
        logger ??= log;
        root ??= rootProvider;
        return this;
    }

    private IServiceProvider Root => root ?? throw new InvalidOperationException(
        "The module registry is not attached to its mesh's container yet — a module service was resolved before the mesh was built.");

    /// <summary>The instance behind forwarded registration <paramref name="index"/> of
    /// <paramref name="module"/>'s CURRENT generation — what every root forwarder resolves.</summary>
    public object ResolveModuleService(string module, int index) =>
        (Current(module)?.Services ?? throw new InvalidOperationException(
            $"Module {module} holds no service scope in this mesh."))
        .Resolve(Root, index);

    /// <summary>The scope of <paramref name="module"/>'s CURRENT generation.</summary>
    public IServiceProvider? ModuleScope(string module) =>
        Current(module)?.Services?.Scope(Root);

    internal Task StartAllModuleHosted(ModuleGeneration generation, CancellationToken ct, ILogger? log) =>
        generation.Services is { } services
            ? services.StartAllHosted(Root, ct, log ?? logger)
            : Task.CompletedTask;

    /// <summary>
    /// Prepares the root services of <paramref name="to"/> — the generation replacing
    /// <paramref name="from"/> — for a live swap: its delegates run against the SAME root prefix the
    /// boot generation saw, and the result must route the same way and keep the same shape, because
    /// the root's forwarders were laid out at boot and are what every consumer holds. Returns why it
    /// cannot be swapped live, or null when <paramref name="to"/> now carries its services.
    /// </summary>
    public string? PrepareServices(ModuleGeneration from, ModuleGeneration to, ModuleContributions contributions)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(contributions);
        var configurations = contributions.ServiceConfigurations;
        if (from.Contributions is { } running && !running.BuilderHooks.Shape.SequenceEqual(contributions.BuilderHooks.Shape))
            return "the new generation's builder hook changed what was applied once at boot (autocomplete exclusions) — "
                   + $"was [{string.Join(", ", running.BuilderHooks.Shape)}], is [{string.Join(", ", contributions.BuilderHooks.Shape)}]";
        if (!from.RootServiceBlockers.IsEmpty)
            return "the running generation's root services went straight into the root container at boot: "
                   + string.Join("; ", from.RootServiceBlockers);
        if (from.Services is not { } old)
            return configurations.Count == 0
                ? null
                : "the new generation registers root services and the running one did not — the root's forwarders are laid out at boot";
        ModuleServices fresh;
        try
        {
            fresh = ModuleServices.Probe(to.Name, to.Context, configurations, old.Prefix);
        }
        catch (Exception exception)
        {
            return $"the new generation's root services could not be built: {exception.GetType().Name}: {exception.Message}";
        }
        if (!fresh.Blockers.IsEmpty)
            return "the new generation's root services cannot be served from its own scope: " + string.Join("; ", fresh.Blockers);
        if (!fresh.Shape.SequenceEqual(old.Shape))
            return "the new generation's root services changed shape (the platform services it forwards to the root differ) — "
                   + $"was [{string.Join(", ", old.Shape)}], is [{string.Join(", ", fresh.Shape)}]";
        to.Services = fresh;
        return null;
    }

    /// <summary>
    /// Re-applies <paramref name="generation"/>'s MESH-level contributions to the RUNNING mesh: its mesh-hub
    /// configuration (attributes, address types, builder hook — each measured at load to mutate only the
    /// configuration it is handed, i.e. its type registry) to <paramref name="meshHub"/>'s live
    /// configuration, and its mesh types to the mesh's shared type registry. The retired generation's
    /// entries are demoted by the registries themselves when its context unloads.
    /// </summary>
    public void ApplyToRunningMesh(ModuleGeneration generation, MeshWeaver.Messaging.IMessageHub meshHub)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(meshHub);
        if (generation.Contributions is not { } contributions)
            return;
        foreach (var configure in contributions.AllMeshHubConfigurations)
            configure(meshHub.Configuration);
        if (contributions.BuilderHooks.MeshTypes.IsEmpty)
            return;
        var registry = Root.GetRequiredService<MeshWeaver.Domain.ITypeRegistry>();
        foreach (var (type, name) in contributions.BuilderHooks.MeshTypes)
            registry.WithType(type, name);
    }

    /// <summary>
    /// Moves the hosted services of a swapped module from <paramref name="from"/> to
    /// <paramref name="to"/>: stops what the old generation started, then starts the same registrations
    /// — every hosted registration the new generation has, which may differ from the old one's. Task-shaped
    /// because <c>IHostedService</c> is.
    /// </summary>
    public Task HandOverHosted(ModuleGeneration from, ModuleGeneration to, CancellationToken ct)
    {
        var stopping = from.Services?.StopHosted(ct, logger) ?? Task.CompletedTask;
        return stopping.ContinueWith(
                _ => to.Services is { } fresh
                    ? fresh.StartAllHosted(Root, ct, logger)
                    : Task.CompletedTask,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>
    /// Loads the module whose entry DLL is <paramref name="entryLocation"/> into a FRESH collectible
    /// context. Not yet current — the caller materialises its contributions and then calls
    /// <see cref="Commit"/>, or <see cref="Discard"/> when they fail. Throws what the load throws.
    /// </summary>
    public ModuleGeneration Load(string entryLocation) => LoadInto(entryLocation, null);

    /// <summary>
    /// Loads like <see cref="Load(string)"/>, but INSIDE <paramref name="stage"/>. A distinct name,
    /// never an overload of <c>Load</c>: an added overload turns every existing
    /// <c>&lt;see cref="Load"/&gt;</c> into CS0419 under <c>-warnaserror</c>, here and in dependents.
    /// The new context
    /// binds every module the stage holds to the STAGED generation rather than the current one, so a
    /// swap can load and materialise a module's dependents against the module's new generation
    /// before ANY of them is made current (#6128 review: committing first published N+1, its
    /// endpoints included, while the swap could still fail).
    /// </summary>
    public ModuleGeneration LoadStaged(string entryLocation, ModuleSwapStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return LoadInto(entryLocation, stage);
    }

    private ModuleGeneration LoadInto(string entryLocation, ModuleSwapStage? stage)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var fullPath = Path.GetFullPath(entryLocation);
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var directory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        var context = new ModuleLoadContext(this, name, directory, Interlocked.Increment(ref sequence), stage);
        try
        {
            var assembly = context.LoadFromAssemblyPath(fullPath);
            return new ModuleGeneration(assembly.GetName().Name ?? name, fullPath, context, assembly);
        }
        catch
        {
            // Nothing ran in it and nothing holds it: unload at once so a refused generation leaves
            // no context behind.
            context.Unload();
            throw;
        }
    }

    /// <summary>Records <paramref name="contributions"/> as what <paramref name="generation"/>
    /// contributes, and each of its nodes as owned by its module.</summary>
    public void SetContributions(ModuleGeneration generation, ModuleContributions contributions)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(contributions);
        generation.Contributions = contributions;
        foreach (var node in contributions.AllNodes)
            nodeOwners.AddOrUpdate(node, generation.Name);
    }

    /// <summary>The module that contributed <paramref name="node"/> (by reference), or null.</summary>
    public string? OwnerOf(MeshNode node) =>
        nodeOwners.TryGetValue(node, out var owner) ? owner : null;

    /// <summary>
    /// The nodes the CURRENT generation of <paramref name="moduleName"/> contributes — empty when
    /// the module is not held or its contributions are not materialised.
    /// </summary>
    public IReadOnlyCollection<MeshNode> CurrentNodes(string moduleName) =>
        Current(moduleName)?.Contributions?.AllNodes ?? [];

    /// <summary>
    /// Makes <paramref name="generation"/> the current one for its module and returns the one it
    /// replaces (null on first install). The replaced generation keeps running for whoever still
    /// leases it; the caller retires it once the hubs bound to it are gone.
    /// </summary>
    public ModuleGeneration? Commit(ModuleGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ModuleGeneration? replaced = null;
        current.AddOrUpdate(generation.Name, generation, (_, previous) =>
        {
            replaced = previous;
            return generation;
        });
        Bump();
        return ReferenceEquals(replaced, generation) ? null : replaced;
    }

    /// <summary>
    /// Makes every generation <paramref name="stage"/> holds current, in the order they were staged,
    /// only AFTER all of them loaded, materialised and prepared, with ONE <see cref="VersionChanged"/>
    /// emission, so no reader of the generations (the module endpoint table, the view registrations,
    /// the static-node catalog) ever observes part of a swap, or a swap that can still fail. Seals
    /// the stage: from here on its contexts resolve modules through the current generations.
    /// </summary>
    public void CommitAll(ModuleSwapStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        foreach (var generation in stage.Seal())
            current[generation.Name] = generation;
        Bump();
    }

    /// <summary>
    /// Unloads every generation <paramref name="stage"/> holds. None of them was ever current, so
    /// nothing was routed to them and <see cref="Discard"/>'s "loaded but never committed" holds.
    /// </summary>
    public void DiscardStaged(ModuleSwapStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        foreach (var generation in stage.Seal())
            Discard(generation);
    }

    /// <summary>
    /// Withdraws <paramref name="generation"/> as its module's current generation when it still is
    /// one — the undo of a <see cref="Commit"/> whose contributions then failed and that replaced
    /// nothing. A no-op when another generation is current.
    /// </summary>
    public void Uncommit(ModuleGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (current.TryRemove(new KeyValuePair<string, ModuleGeneration>(generation.Name, generation)))
            Bump();
    }

    /// <summary>Unloads a generation that was loaded but never committed (its contributions failed).</summary>
    public void Discard(ModuleGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (current.TryGetValue(generation.Name, out var live) && ReferenceEquals(live, generation))
            throw new InvalidOperationException(
                $"'{generation.Context.Name}' is the current generation of {generation.Name}; retire it instead.");
        generation.Services?.Dispose();
        RetireCore(generation.Context);
        generation.Context.Unload();
    }

    /// <summary>
    /// Retires <paramref name="generation"/>: removes it from <see cref="Generations"/> if it is
    /// still current, then unloads it once nothing holds a lease on it — and NOT AT ALL if that does
    /// not happen within <paramref name="budget"/> (a leak, deliberately chosen over a crash). Cold:
    /// the caller subscribes. Emits whether the unload was started.
    /// </summary>
    public IObservable<bool> Retire(ModuleGeneration generation, TimeSpan budget) =>
        Observable.Defer(() =>
        {
            ArgumentNullException.ThrowIfNull(generation);
            current.TryRemove(new KeyValuePair<string, ModuleGeneration>(generation.Name, generation));
            // Its singletons go first — they are this generation's objects and must not outlive it.
            generation.Services?.Dispose();
            var retirement = RetireCore(generation.Context);
            return Leases.UnloadWhenQuiesced(generation.Context, budget, logger, generation.Context.Name)
                .Do(unloaded =>
                {
                    if (!unloaded)
                        retirement?.Faulted(new InvalidOperationException(
                            $"Module generation '{generation.Context.Name}' ({generation.Location}) was KEPT loaded: "
                            + $"it still had {Leases.InFlight(generation.Context)} in-flight lease(s) after {budget}."));
                });
        });

    /// <summary>
    /// The current entry assembly of the module named <paramref name="simpleName"/>, or null — the
    /// explicit resolution a NodeType build or a script session asks for a module it references,
    /// since no default-context binding may ever hand one out (see <see cref="ModuleLoadContext"/>).
    /// </summary>
    public Assembly? Resolve(string simpleName) =>
        !string.IsNullOrEmpty(simpleName) && current.TryGetValue(simpleName, out var generation)
            ? generation.Assembly
            : null;

    /// <summary>
    /// <paramref name="assemblyName"/> as a context that is NOT a module asks for it at run time —
    /// a kernel script session: a module's entry assembly, or one of a module's PRIVATE
    /// dependencies, bound through that module's own context and returned with a LEASE on it. Null
    /// when no candidate module holds or ships it, and null when the module context answers it from
    /// the default context (the platform), so the caller falls through to the default context for
    /// exactly the same answer.
    ///
    /// <para><b>Why the module's dependencies too.</b> A module ships its private closure beside its
    /// entry DLL (Microsoft.Graph and Kiota beside <c>MeshWeaver.Mail.MicrosoftGraph</c>), and the
    /// image does not. A script compiles against that closure — the shared metadata resolver finds
    /// the file next to the module DLL that references it — so binding only the entry assembly made
    /// the script compile and then throw <see cref="FileNotFoundException"/> the moment it touched a
    /// type from the closure: the default context cannot see the module's directory. Loading the
    /// dependency through the module's context also gives the script the SAME identity the module's
    /// own code binds, so a value crosses between them with its type intact.</para>
    ///
    /// <para><b>Which generations.</b> <paramref name="among"/> — the generations the caller compiled
    /// against, so compile and run see the same module code — or, when null, the current ones. A
    /// candidate is used only while it is still CURRENT, and it is leased before anything is loaded
    /// from it and re-checked after: a generation a swap has already displaced may be unloading, and
    /// loading from it is the use-after-unload this registry's leases exist to prevent. A retirement
    /// that starts after the lease waits for it.</para>
    ///
    /// <para><b>Resolution order.</b> (1) a candidate's entry assembly; (2) an assembly a
    /// candidate's context already holds; (3) a file of that name in a candidate's generation
    /// directory, loaded through that candidate's context — whose own order puts the platform first.
    /// Candidates are visited in name order, and one whose version is lower than the one requested
    /// is skipped.</para>
    /// </summary>
    public ModuleBinding? ResolveDependency(AssemblyName assemblyName, IReadOnlyCollection<ModuleGeneration>? among = null)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        if (assemblyName.Name is not { Length: > 0 } name)
            return null;
        var candidates = (among ?? current.Values.ToArray())
            .OrderBy(g => g.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var generation in candidates)
            if (string.Equals(generation.Name, name, StringComparison.Ordinal)
                && Satisfies(generation.Assembly.GetName(), assemblyName)
                && LeaseIfCurrent(generation) is { } lease)
                return new ModuleBinding(generation.Assembly, generation.Context, lease);

        foreach (var generation in candidates)
        {
            if (LeaseIfCurrent(generation) is not { } lease)
                continue;
            foreach (var assembly in generation.Context.Assemblies)
                if (assembly.GetName() is { } held
                    && string.Equals(held.Name, name, StringComparison.Ordinal)
                    && Satisfies(held, assemblyName))
                    return new ModuleBinding(assembly, generation.Context, lease);
            lease.Dispose();
        }

        foreach (var generation in candidates)
        {
            var candidate = Path.Combine(generation.Context.Directory, name + ".dll");
            if (!File.Exists(candidate) || !Satisfies(NameOf(candidate), assemblyName)
                || LeaseIfCurrent(generation) is not { } lease)
                continue;
            Assembly loaded;
            try
            {
                loaded = generation.Context.LoadFromAssemblyName(assemblyName);
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                lease.Dispose();
                continue;
            }
            if (AssemblyLoadContext.GetLoadContext(loaded) is ModuleLoadContext owner)
            {
                if (ReferenceEquals(owner, generation.Context))
                    return new ModuleBinding(loaded, owner, lease);
                // Served from a module this one depends on: lease THAT generation instead, on the
                // same terms — current, or not at all.
                var ownerLease = Current(owner.ModuleName) is { } ownerGeneration
                                 && ReferenceEquals(ownerGeneration.Context, owner)
                    ? LeaseIfCurrent(ownerGeneration)
                    : null;
                lease.Dispose();
                return ownerLease is null ? null : new ModuleBinding(loaded, owner, ownerLease);
            }
            // The platform's (or the image's) answer is the default context's to give.
            lease.Dispose();
            return null;
        }
        return null;
    }

    // Leases `generation` and keeps the lease only if it is STILL current after the lease is taken:
    // Retire removes a generation from `current` BEFORE it waits for quiescence, so a lease that
    // sees it current is one the retirement will wait for.
    private IDisposable? LeaseIfCurrent(ModuleGeneration generation)
    {
        var lease = Leases.Enter(generation.Context);
        if (current.TryGetValue(generation.Name, out var live) && ReferenceEquals(live, generation))
            return lease;
        lease.Dispose();
        return null;
    }

    private static AssemblyName? NameOf(string path)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path);
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            return null;
        }
    }

    // A context may not answer a request with a LOWER version than the one asked for.
    private static bool Satisfies(AssemblyName? candidate, AssemblyName requested) =>
        candidate is not null
        && (requested.Version is null || (candidate.Version is { } version && version >= requested.Version));

    /// <summary>
    /// The current generations that depend — directly or transitively — on
    /// <paramref name="moduleName"/>: what has to be swapped with it, because each one is bound to
    /// that module's types.
    /// </summary>
    public IReadOnlyList<ModuleGeneration> DependentsOf(string moduleName)
    {
        var result = new List<ModuleGeneration>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { moduleName };
        var frontier = new Queue<string>([moduleName]);
        var snapshot = current.Values.ToArray();
        while (frontier.TryDequeue(out var name))
            foreach (var generation in snapshot)
                if (generation.Context.DependsOn.Contains(name) && seen.Add(generation.Name))
                {
                    result.Add(generation);
                    frontier.Enqueue(generation.Name);
                }
        return result;
    }

    /// <summary>
    /// Step 2 of <see cref="ModuleLoadContext"/>'s resolution: <paramref name="name"/> as another
    /// module's entry assembly, or as an assembly already loaded by a module
    /// <paramref name="requester"/> depends on.
    /// </summary>
    internal Assembly? ResolveForDependent(string name, ModuleLoadContext requester)
    {
        // A staged context binds the stage's generation first: the swap it belongs to makes them
        // current together, so this is the binding it will have once the swap commits.
        ModuleGeneration? Held(string module) =>
            requester.Stage?.Get(module) ?? (current.TryGetValue(module, out var generation) ? generation : null);

        if (!string.Equals(name, requester.ModuleName, StringComparison.Ordinal)
            && Held(name) is { } module)
        {
            requester.RecordDependency(name);
            return module.Assembly;
        }

        foreach (var dependency in requester.DependsOn)
        {
            if (Held(dependency) is not { } held)
                continue;
            foreach (var assembly in held.Context.Assemblies)
                if (string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal))
                    return assembly;
        }
        return null;
    }

    private CollectibleContextUnloads.Retirement? RetireCore(ModuleLoadContext context)
    {
        if (unloads is null)
            return null;
        var retirement = unloads.Retire(context.Name ?? ModuleLoadContext.NamePrefix);
        // 🚨 Held in a FIELD of the context — never in an Unloading closure. Unload() swaps the
        // Unloading delegate out to null as it raises it, so a sentinel captured there is unreachable
        // the moment the unload STARTS and reports "collected" while the context is still alive (the
        // negative control in ModulesRunInTheirOwnContextTest caught exactly that). Referenced only by
        // the context, it dies in the same collection as the context — after the runtime has destroyed
        // its LoaderAllocator — and its finalizer reports the retirement collected (the shape
        // NodeAssemblyLoadContext uses, Plugins#1605).
        context.HoldRetirementSentinel(new RetirementSentinel(retirement));
        return retirement;
    }

    private sealed class RetirementSentinel(CollectibleContextUnloads.Retirement retirement)
    {
        ~RetirementSentinel() => retirement.Collected();
    }

    /// <summary>
    /// Retires every generation still held — the mesh is going down, so no hub can enter a module
    /// any more. A generation somebody still leases is left loaded rather than unloaded under them,
    /// and says so.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        foreach (var generation in current.Values.ToArray())
        {
            current.TryRemove(generation.Name, out _);
            generation.Services?.Dispose();
            var retirement = RetireCore(generation.Context);
            var inFlight = Leases.InFlight(generation.Context);
            if (inFlight > 0)
            {
                logger?.LogError(
                    "NOT unloading module generation {Context} at mesh teardown: {InFlight} lease(s) still held.",
                    generation.Context.Name, inFlight);
                retirement?.Faulted(new InvalidOperationException(
                    $"Module generation '{generation.Context.Name}' was kept loaded at teardown: {inFlight} lease(s) held."));
                continue;
            }
            generation.Context.Unload();
        }
    }
}

/// <summary>
/// The generations of ONE live swap, loaded and materialised but not yet current. A context loaded
/// into the stage (<see cref="ModuleContexts.LoadStaged"/>) binds these in place
/// of the current generations, so a module's dependents re-bind to its new generation before anything
/// is published. Made current all at once by <see cref="ModuleContexts.CommitAll"/>, or unloaded by
/// <see cref="ModuleContexts.DiscardStaged"/>; either seals it. Used by one swap at a time (the swap
/// pipeline is serial).
/// </summary>
public sealed class ModuleSwapStage
{
    private ImmutableList<ModuleGeneration> staged = ImmutableList<ModuleGeneration>.Empty;
    private int sealedFlag;

    /// <summary>Adds <paramref name="generation"/> to the stage; later contexts in the stage bind it.</summary>
    public void Add(ModuleGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (Volatile.Read(ref sealedFlag) != 0)
            throw new InvalidOperationException("The swap stage is sealed: it was committed or discarded.");
        ImmutableInterlocked.Update(ref staged, list => list.Add(generation));
    }

    /// <summary>The staged generation of <paramref name="moduleName"/>, or null; always null once sealed.</summary>
    public ModuleGeneration? Get(string moduleName) =>
        Volatile.Read(ref sealedFlag) != 0
            ? null
            : Volatile.Read(ref staged).LastOrDefault(g => string.Equals(g.Name, moduleName, StringComparison.Ordinal));

    internal ImmutableList<ModuleGeneration> Seal()
    {
        if (Interlocked.Exchange(ref sealedFlag, 1) != 0)
            throw new InvalidOperationException("The swap stage was already committed or discarded.");
        return Volatile.Read(ref staged);
    }
}
