using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Sanctioned dedicated identity for the process-wide
/// <see cref="MeshNodeStreamCache"/> hydrator.
///
/// <para>The cache subscribes to per-path streams under this identity at hub
/// startup. The identity is granted ONLY <see cref="Mesh.Security.Permission.Read"/>
/// in <c>SecurityService.GetEffectivePermissions</c> — it cannot create,
/// update, or delete. Writes attempted under this identity are denied by the
/// normal access-control path because no <c>AccessAssignment</c> exists
/// granting it write operations.</para>
///
/// <para>The constant is <c>internal</c> so external assemblies cannot
/// reference it accidentally. Tests that need to verify the boundary use the
/// string literal directly — that mirrors what a malicious caller would have
/// to do, and the violation tests prove that even with the literal in hand,
/// writes fail and only Read works (which is itself double-gated at
/// <c>MeshNodeStreamCache.GetStream</c> per requesting user).</para>
///
/// <para>See <c>Doc/Architecture/AccessContextPropagation.md</c> →
/// "Sanctioned exceptions — fine-grained, exact, controlled" for the
/// define / grant / test contract this identity exemplifies.</para>
/// </summary>
internal static class MeshNodeCacheIdentity
{
    /// <summary>
    /// The dedicated address that stamps as principal on the cache's
    /// hydrating <c>SubscribeRequest</c>. Prefix <c>cache/</c> signals
    /// "this is a cache identity" to anyone reading the value in logs or
    /// stored <c>CreatedBy</c> fields; the suffix names the specific cache.
    /// </summary>
    internal const string Address = "cache/mesh-node-cache";

    /// <summary>
    /// Pre-allocated <see cref="AccessContext"/> for the cache identity. The
    /// name matches the address for log readability — there is no human
    /// behind this identity.
    /// </summary>
    internal static readonly AccessContext Context = new()
    {
        ObjectId = Address,
        Name = Address
    };
}
