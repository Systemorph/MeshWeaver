using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// A kernel script session's module surface, compile AND run, pinned to ONE set of module
/// generations: the ones current when the session built its reference set.
///
/// <para><b>Compile and run must agree.</b> The session compiles against the PINNED generations'
/// entry assemblies (<see cref="SessionAssemblies"/>), and the shared metadata resolver
/// (<see cref="SharedScriptMetadataResolver"/>) finds a module's private dependency as the file next
/// to the module DLL that references it — the pinned generation's directory. At run time the
/// session's load context asks <see cref="Bind"/> for any name its submissions do not define and no
/// cell-surface pack declares, and that binds through <see cref="ModuleContexts.ResolveDependency"/>
/// from the SAME pinned generations, through the module's own context. Binding the current
/// generation instead would let a live swap between the two make a submission compile against N and
/// run against N+1. A pinned generation that a swap has displaced before the session first bound it
/// is not bound at all (it may already be unloading): the session fails to load it, and a new
/// session binds the new generation.</para>
///
/// <para><b>The lease.</b> Every module context this session binds into stays leased on the
/// registry's <see cref="ModuleContexts.Leases"/> until <see cref="Dispose"/> — the session's end —
/// because anything a submission left behind (a subscription, a rendered control, a stored delegate)
/// can call into it after the submission returned. The lease is taken by the registry before
/// anything is loaded, so a binding never comes from a context that has begun unloading; a swap
/// retires a bound generation only once the session has ended.</para>
///
/// <para>The pinned generations are held WEAKLY: a pinned generation nothing else holds has been
/// retired, and the session would refuse it anyway — so pinning never keeps a retired generation
/// loaded. Thread-safe: the runtime may call a load context's <c>Load</c> from several threads.</para>
/// </summary>
internal sealed class ModuleScriptBindings : IDisposable
{
    private readonly ModuleContexts? modules;
    private readonly ImmutableArray<WeakReference<ModuleGeneration>> pinned;
    private readonly ConcurrentDictionary<AssemblyLoadContext, IDisposable> leases = new();
    private int disposed;

    private ModuleScriptBindings(ModuleContexts? modules, IReadOnlyCollection<ModuleGeneration> generations)
    {
        this.modules = modules;
        pinned = [.. generations.Select(g => new WeakReference<ModuleGeneration>(g))];
    }

    /// <summary>Pins the generations of <paramref name="modules"/> that are current NOW.</summary>
    public static ModuleScriptBindings PinCurrent(ModuleContexts? modules) =>
        new(modules, modules?.Generations ?? []);

    /// <summary>The pinned generations still alive.</summary>
    private IReadOnlyCollection<ModuleGeneration> Pinned =>
        [.. pinned.Select(w => w.TryGetTarget(out var g) ? g : null).OfType<ModuleGeneration>()];

    /// <summary>
    /// The session's compile surface with every installed module replaced by its PINNED
    /// generation's entry assembly — the counterpart of <see cref="Bind"/>.
    /// </summary>
    public IReadOnlyCollection<Assembly> SessionAssemblies(IServiceProvider serviceProvider)
    {
        var byName = Pinned.ToDictionary(g => g.Name, g => g.Assembly, StringComparer.Ordinal);
        var assemblies = new HashSet<Assembly>(byName.Values);
        foreach (var assembly in MeshScriptEnvironment.SessionAssemblies(serviceProvider))
            if (assembly.GetName().Name is not { } name || !byName.ContainsKey(name))
                assemblies.Add(assembly);
        return assemblies;
    }

    /// <summary>The module contexts this session currently leases — diagnostics and tests.</summary>
    public IReadOnlyCollection<AssemblyLoadContext> Leased => leases.Keys.ToArray();

    /// <summary>
    /// The pinned module assembly <paramref name="name"/> binds to, its context leased for the
    /// session's lifetime; null when no pinned module holds or ships it (the default context answers
    /// then), when its pinned generation is no longer current, or once the session has ended.
    /// </summary>
    public Assembly? Bind(AssemblyName name)
    {
        if (modules is null || Volatile.Read(ref disposed) != 0)
            return null;
        if (modules.ResolveDependency(name, Pinned) is not { } binding)
            return null;
        if (!leases.TryAdd(binding.Context, binding.Lease))
            binding.Lease.Dispose(); // already leased by this session
        // A Dispose that ran before the add would never see this lease.
        else if (Volatile.Read(ref disposed) != 0 && leases.TryRemove(binding.Context, out var late))
            late.Dispose();
        return binding.Assembly;
    }

    /// <summary>Releases every lease — the session has ended and nothing of it can call in any more.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        foreach (var context in leases.Keys.ToArray())
            if (leases.TryRemove(context, out var lease))
                lease.Dispose();
    }
}
