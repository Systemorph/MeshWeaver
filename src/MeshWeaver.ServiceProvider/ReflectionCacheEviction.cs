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
        ReflectionCacheClearPredicate references = (member, referencedAssemblies) =>
        {
            foreach (var assembly in referencedAssemblies)
                if (AssemblyLoadContext.GetLoadContext(assembly) == loadContext)
                    return true;
            // 🚨 The assembly list is not enough: a key of OptionsFactory<ModuleOptions> names only
            // Microsoft.Extensions.Options, while its generic ARGUMENT lives in the unloading context
            // (measured — the cache entry that pinned a swapped-out module generation).
            return member is Type type && Names(type, loadContext);
        };
        // Access Shared fresh each call — it is a WeakReference-backed singleton and must
        // never be stored (Autofac's own guidance on the property).
        ReflectionCacheSet.Shared.Clear(references);
        // 🚨 …and the one cache that set can LOSE. Autofac.Extensions.DependencyInjection's
        // FromKeyedServicesUsageCache registers its static cache with ReflectionCacheSet.Shared ONCE,
        // in its static constructor. Shared is weakly held, so once it has been collected and
        // re-created the new set no longer knows that cache, and the Clear above never reaches it.
        // Measured (live module swap, policy module-live-update-default): a heap dump of a retained
        // module generation showed this cache's ConcurrentDictionary as the ONLY strong root of the
        // generation's LoaderAllocator. Cleared directly, through the same predicate.
        if (FromKeyedServicesUsageCache is { } cache)
            cache.Clear(references);
    }

    private static bool Names(Type type, AssemblyLoadContext loadContext) =>
        AssemblyLoadContext.GetLoadContext(type.Assembly) == loadContext
        || (type.IsGenericType && !type.IsGenericTypeDefinition
            && type.GetGenericArguments().Any(argument => Names(argument, loadContext)))
        || (type.HasElementType && type.GetElementType() is { } element && Names(element, loadContext));

    /// <summary>Whether the Autofac.Extensions.DependencyInjection static cache that
    /// <see cref="ReflectionCacheSet.Shared"/> can lose is reachable — pinned true by a test, so a
    /// future Autofac that renames it fails loudly instead of leaking silently.</summary>
    public static bool FromKeyedServicesUsageCacheIsReachable => FromKeyedServicesUsageCache is not null;

    private static IReflectionCache? FromKeyedServicesUsageCache =>
        typeof(Autofac.Extensions.DependencyInjection.AutofacServiceProvider).Assembly
            .GetType("Autofac.Extensions.DependencyInjection.FromKeyedServicesUsageCache")
            ?.GetField("_reflectionCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) as IReflectionCache;
}
