using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh.Threading;
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
}

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
public sealed class ModuleContexts : IDisposable
{
    private readonly ConcurrentDictionary<string, ModuleGeneration> current = new(StringComparer.Ordinal);
    // Which module contributed a mesh node — by REFERENCE, weakly: the seed node list asks it so a
    // module's nodes can be served from its CURRENT generation once the generation that contributed
    // them at boot has been swapped out (see StaticMeshNodeListProvider).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshNode, string> nodeOwners = new();
    private long sequence;
    private CollectibleContextUnloads? unloads;
    private ILogger? logger;
    private int disposed;

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
    {
        unloads ??= collectibleUnloads;
        logger ??= log;
        return this;
    }

    /// <summary>
    /// Loads the module whose entry DLL is <paramref name="entryLocation"/> into a FRESH collectible
    /// context. Not yet current — the caller materialises its contributions and then calls
    /// <see cref="Commit"/>, or <see cref="Discard"/> when they fail. Throws what the load throws.
    /// </summary>
    public ModuleGeneration Load(string entryLocation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var fullPath = Path.GetFullPath(entryLocation);
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var directory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        var context = new ModuleLoadContext(this, name, directory, Interlocked.Increment(ref sequence));
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
        foreach (var node in contributions.Nodes)
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
        Current(moduleName)?.Contributions?.Nodes ?? [];

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
        return ReferenceEquals(replaced, generation) ? null : replaced;
    }

    /// <summary>
    /// Withdraws <paramref name="generation"/> as its module's current generation when it still is
    /// one — the undo of a <see cref="Commit"/> whose contributions then failed and that replaced
    /// nothing. A no-op when another generation is current.
    /// </summary>
    public void Uncommit(ModuleGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        current.TryRemove(new KeyValuePair<string, ModuleGeneration>(generation.Name, generation));
    }

    /// <summary>Unloads a generation that was loaded but never committed (its contributions failed).</summary>
    public void Discard(ModuleGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (current.TryGetValue(generation.Name, out var live) && ReferenceEquals(live, generation))
            throw new InvalidOperationException(
                $"'{generation.Context.Name}' is the current generation of {generation.Name}; retire it instead.");
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
        if (!string.Equals(name, requester.ModuleName, StringComparison.Ordinal)
            && current.TryGetValue(name, out var module))
        {
            requester.RecordDependency(name);
            return module.Assembly;
        }

        foreach (var dependency in requester.DependsOn)
        {
            if (!current.TryGetValue(dependency, out var held))
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
