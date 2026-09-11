using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using System.Text.Json;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Empties System.Text.Json's PROCESS-STATIC member-accessor cache when a collectible node context
/// starts unloading — the System.Text.Json twin of <c>ReflectionCacheEviction</c> (Autofac).
///
/// <para><b>Why (Plugins#1605, measured).</b> On CoreCLR, STJ builds property getters/setters and
/// constructors as <see cref="System.Reflection.Emit.DynamicMethod"/>s and keeps them in a static
/// cache with a 1-second sliding expiry, evicted by a 200 ms timer
/// (<c>ReflectionEmitCachingMemberAccessor</c>). A dynamic method's scope holds the
/// <see cref="System.RuntimeTypeHandle"/> of every type its IL touches, so serialising ONE instance of
/// a NodeType-compiled type roots that type's <c>LoaderAllocator</c> from a static field. A heap dump
/// taken at the end of a FutuRe teardown (gcroot, 2026-09-11) shows exactly that as the ONLY strong
/// root of both contexts that teardown could not collect: <c>ReflectionEmitCachingMemberAccessor</c>
/// → <c>Cache</c> → <c>CacheEntry</c> → <c>Action&lt;object, string&gt;</c> → <c>DynamicMethod</c>
/// → <c>DynamicResolver</c> → <c>DynamicScope</c> → <c>RuntimeTypeHandle</c> → the collectible
/// <c>RuntimeType</c> → <c>LoaderAllocator</c>. So an unload "completed" whenever STJ's timer happened
/// to fire — typically a second later, while the NEXT mesh was already being built — which is the
/// disposal-in-progress-while-the-next-instance-starts overlap every readable crash dump caught.</para>
///
/// <para><b>How.</b> Through STJ's own published hook, not its internals: the assembly declares a
/// <see cref="MetadataUpdateHandlerAttribute"/> whose handler exposes the static
/// <c>ClearCache(Type[]?)</c> the hot-reload agent calls after a metadata update. It clears the
/// member-accessor cache (and the per-options caches STJ tracks only while hot reload is on). Clearing
/// drops shared accessors for every type, not only the unloading one; the cost is a re-emit the next
/// time a NEW options instance resolves a type — live options keep the accessors they already hold.</para>
///
/// <para>Runs inside <c>Unloading</c>, i.e. synchronously within <c>Unload()</c>, before the context
/// is released — the same moment Autofac's cache is purged. Holds no state: the resolved method is an
/// immutable lookup computed once (NoStaticState allows <c>static readonly</c> constants).</para>
/// </summary>
internal static class JsonMemberAccessorCacheEviction
{
    private static readonly MethodInfo? ClearCache = typeof(JsonSerializer).Assembly
        .GetCustomAttributes<MetadataUpdateHandlerAttribute>()
        .Select(attribute => attribute.HandlerType.GetMethod(
            "ClearCache",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(Type[])]))
        .FirstOrDefault(method => method is not null);

    /// <summary>
    /// Whether this runtime's System.Text.Json publishes the hook. Pinned true by a control: if a
    /// future STJ drops it, the eviction must fail a test, not silently stop happening.
    /// </summary>
    public static bool IsAvailable => ClearCache is not null;

    /// <summary>The <c>Unloading</c> handler. Static, so it roots nothing.</summary>
    public static void EvictFor(AssemblyLoadContext loadContext)
        => ClearCache?.Invoke(null, [null]);
}
