using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// The run-time half of a kernel script session's module surface: what the session's load context
/// asks for a name its submissions do not define and no cell-surface pack declares — a module's
/// entry assembly, or one of a module's PRIVATE dependencies — bound through
/// <see cref="ModuleContexts.ResolveDependency"/>, i.e. through that module's own collectible
/// context.
///
/// <para><b>Compile and run must agree.</b> At compile time the shared metadata resolver
/// (<see cref="SharedScriptMetadataResolver"/>) finds a module's private dependency as the file next
/// to the module DLL that references it, so the script compiles against it. The default context
/// cannot see a module's directory, so a run-time bind that knew only entry assemblies made such a
/// script compile and then throw <see cref="FileNotFoundException"/> on the first type it touched
/// from the dependency. This binds the same file, in the module's context, with the identity the
/// module's own code uses.</para>
///
/// <para><b>The lease.</b> Every module context this session binds into is leased on the module
/// registry's <see cref="ModuleContexts.Leases"/> until <see cref="Dispose"/> — the session's end —
/// because anything a submission left behind (a subscription, a rendered control, a stored delegate)
/// can call into it after the submission returned. A live swap of that module therefore retires the
/// old generation only once the session has ended; the session itself keeps the generation it bound,
/// exactly as it keeps a cell-surface NodeType generation.</para>
///
/// <para>Thread-safe: the runtime may call a load context's <c>Load</c> from several threads.</para>
/// </summary>
internal sealed class ModuleScriptBindings(ModuleContexts? modules) : IDisposable
{
    private readonly ConcurrentDictionary<AssemblyLoadContext, IDisposable> leases = new();
    private int disposed;

    /// <summary>The module contexts this session currently leases — diagnostics and tests.</summary>
    public IReadOnlyCollection<AssemblyLoadContext> Leased => leases.Keys.ToArray();

    /// <summary>
    /// The module assembly <paramref name="name"/> binds to, leasing its context for the session's
    /// lifetime; null when no current module holds or ships it (the default context answers then),
    /// or once the session has ended.
    /// </summary>
    public Assembly? Bind(AssemblyName name)
    {
        if (modules is null || Volatile.Read(ref disposed) != 0)
            return null;
        if (modules.ResolveDependency(name) is not { } binding)
            return null;
        if (!leases.ContainsKey(binding.Context))
        {
            var lease = modules.Leases.Enter(binding.Context);
            if (!leases.TryAdd(binding.Context, lease))
                lease.Dispose();
            // A Dispose that ran between the check above and the add would never see this lease.
            else if (Volatile.Read(ref disposed) != 0 && leases.TryRemove(binding.Context, out var late))
                late.Dispose();
        }
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
