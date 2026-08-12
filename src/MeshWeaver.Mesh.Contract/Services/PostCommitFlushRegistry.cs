using System.Collections.Concurrent;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// 🚨 Per-path DURABLE-VERSION HIGH-WATER for the post-commit flush — the record of what the
/// cross-hub patch path has ALREADY written to storage, so the per-node persistence sampler does
/// not write it a second time (#1249).
///
/// <para><b>The defect it closes.</b> One patch-driven own-node change reached durable storage by
/// TWO independent routes:</para>
/// <list type="number">
///   <item><b>The post-commit flush.</b> <c>DataExtensions.ApplyMeshNodePatchInTurn</c> chains
///     <c>IPostCommitFlush.Flush</c> off the reduced stream's post-commit emission and the caller's
///     <c>PatchDataResponse</c> ack off THAT — so the row is durable before <c>stream.Update</c>
///     returns. This is what gives cross-hub writes read-after-write; it is the authoritative
///     route.</item>
///   <item><b>The per-node persistence sampler.</b> <c>MeshDataSource</c>'s own-stream
///     <c>Sample(200 ms)</c> posts a <c>SaveMeshNodeRequest</c> for every own-node change, whose
///     handler writes the sampled node. Indispensable for own writes that never went through a
///     patch — a pure duplicate for one that did.</item>
/// </list>
///
/// <para>The two are never ordered against each other: the flush writes from an emission thread,
/// the sampler through the owner's inbox. Under a sustained write rate the row advances while the
/// sampler's message queues, so its write lands as a strict version REGRESSION.
/// <c>MonotonicWriteGuardStorageAdapter</c> correctly refuses it — and then resolves the conflict
/// by merging, which with no common ancestor keeps the string SUPERSET and the array UNION. A
/// deletion the newer write made is therefore silently RE-ADDED. Resurrection is a deliberate
/// trade-off for a GENUINE conflict; this one was manufactured by the framework against itself, on
/// a strictly sequential writer, and it also devalued the guard's alarm into background noise.</para>
///
/// <para><b>Why a mesh-scoped, path-keyed registry.</b> <c>IPostCommitFlush</c> is registered once
/// per mesh (<c>AddMeshCatalog</c>) and routes writes by node path, while the sampler's handler runs
/// on the per-node owner hub — the two do not share a hub-scoped service, so a per-hub stamp on
/// <c>OwnNodeCache</c> is invisible to the flush. Registered at the mesh ROOT (see
/// <see cref="MeshBuilder"/>, alongside <see cref="RecentlyDeletedRegistry"/>) so a hub
/// <c>ServiceProvider</c> lookup falls back to the ONE instance — a hub-level registration would
/// create a second one and each side would consult a registry the other never wrote (the exact trap
/// #839 hit with the deletion registry).</para>
///
/// <para><b>Why a VERSION, and why <c>&lt;=</c> is a safe skip predicate.</b> A reference-identity
/// stamp cannot work here: the sampler's gate chain runs in the SAME synchronous fan-out as the
/// flush and — having subscribed at hub init — runs FIRST, so nothing the flush stamps is visible
/// to it. The mark is therefore read at HANDLER time, once the flush has settled. And two DISTINCT
/// own-node states can never share a version: <c>MeshNodeTypeSource.UpdateImpl</c> re-stamps any own
/// update arriving at (or below) its previous version with <c>MeshNode.NextVersion</c>. So a sampled
/// state at or below the flushed version is either the very state the flush persisted or an older
/// one — never newer content.</para>
///
/// <para>The mark is raised only on a write that actually EMITTED, so a failed flush leaves the
/// sampler as the writer of record; and it is dropped on delete
/// (<see cref="Forget"/>) so a same-id recreate at <c>Version = 1</c> is never mistaken for
/// already-persisted state — the same rule <c>MonotonicWriteGuardStorageAdapter.Forget</c>
/// follows.</para>
///
/// <para>Footprint: one <c>path → long</c> entry per node this process has patched, dropped on
/// delete. Instance state on a mesh-scoped singleton — it dies with the mesh, never a static.</para>
/// </summary>
public sealed class PostCommitFlushRegistry
{
    private readonly ConcurrentDictionary<string, long> highWater = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Monotonically (max) records that <paramref name="version"/> of <paramref name="path"/> is
    /// durable. Called from the post-commit flush on the write's emission.
    /// </summary>
    public void Record(string? path, long version)
    {
        if (string.IsNullOrEmpty(path))
            return;
        highWater.AddOrUpdate(path, version, (_, current) => Math.Max(current, version));
    }

    /// <summary>
    /// The highest version the post-commit flush has made durable for <paramref name="path"/>, or
    /// <c>0</c> when this process has never flushed it (in which case nothing is suppressed).
    /// </summary>
    public long HighWater(string? path)
        => !string.IsNullOrEmpty(path) && highWater.TryGetValue(path, out var version) ? version : 0L;

    /// <summary>
    /// Drops the mark for a deleted path, so a same-id recreate — which legitimately restarts at
    /// <c>Version = 1</c> — is never read as "already persisted".
    /// </summary>
    public void Forget(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            highWater.TryRemove(path, out _);
    }
}
