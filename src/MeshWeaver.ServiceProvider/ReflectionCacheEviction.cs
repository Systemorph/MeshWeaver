#nullable enable
using System.Runtime.Loader;
using Autofac.Core;

namespace MeshWeaver.ServiceProvider;

/// <summary>
/// Evicts entries from Autofac's <b>process-static</b> shared reflection cache
/// (<see cref="ReflectionCacheSet.Shared"/>) that reference assemblies loaded into a
/// collectible <see cref="AssemblyLoadContext"/> which is about to be unloaded.
/// </summary>
/// <remarks>
/// Autofac keys its reflection caches (constructor-binder factories, parameter maps,
/// assembly scans) on <see cref="System.Reflection.MemberInfo"/>/<see cref="System.Reflection.Assembly"/>.
/// Those keys strongly root the declaring assembly and therefore its
/// <see cref="AssemblyLoadContext"/>. MeshWeaver compiles nodes into collectible load
/// contexts (<c>NodeAssemblyLoadContext</c>) and unloads them on recompile / release /
/// teardown. If the shared cache still holds an entry for the unloaded assembly, two
/// things go wrong:
/// <list type="number">
/// <item>the context can never be collected (the cache roots it) — an unbounded leak; and</item>
/// <item>a later, unrelated concurrent <c>GetOrAdd</c> whose key hashes into the same bucket
/// compares against the stale key and dereferences <b>freed metadata</b> →
/// <see cref="AccessViolationException"/> / SIGSEGV.</item>
/// </list>
/// Autofac performs exactly this eviction automatically for scopes created via
/// <c>BeginLoadContextLifetimeScope</c>; because MeshWeaver manages its collectible
/// contexts by hand, it must run the eviction itself — <b>before</b> unload, while the
/// assembly metadata the predicate walks is still valid.
/// </remarks>
public static class ReflectionCacheEviction
{
    /// <summary>
    /// Removes every shared-reflection-cache entry that references an assembly loaded into
    /// <paramref name="loadContext"/>. Safe to call concurrently with cache reads (the
    /// underlying stores are <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>);
    /// call it <b>before</b> unloading so the walked metadata is still live.
    /// </summary>
    /// <param name="loadContext">The collectible context whose entries should be purged.</param>
    public static void EvictFor(AssemblyLoadContext loadContext)
    {
        // Access Shared fresh each call — it is a WeakReference-backed singleton and must
        // never be stored (Autofac's own guidance on the property).
        ReflectionCacheSet.Shared.Clear((_, referencedAssemblies) =>
        {
            foreach (var assembly in referencedAssemblies)
                if (AssemblyLoadContext.GetLoadContext(assembly) == loadContext)
                    return true;
            return false;
        });
    }
}
