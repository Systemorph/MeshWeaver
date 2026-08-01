using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Outermost <see cref="IStorageAdapter"/> decorator that refuses a write whose
/// <see cref="MeshNode.Version"/> would move a node's durable state BACKWARD.
///
/// <para><b>Why.</b> <c>MeshNode.Version</c> is the node's own forward-only revision counter
/// (<c>Doc/Architecture/MeshNodeVersioning.md</c>): every mint goes through
/// <see cref="MeshNode.NextVersion"/>, which is <c>current.Version + 1</c>, and an unchanged
/// node is re-persisted at the version it already carries — so a correctly-produced write is
/// ALWAYS &gt;= the version already stored. A write that
/// regresses is therefore never a legitimate newer state — it is a stale snapshot that some
/// component adopted as live (a cache-seeded reactivation, a lagging change-feed echo, a
/// debounce buffer that outlived its state) about to overwrite acked, durable data. Before
/// this guard the store took it silently: the observed production shape was
/// <c>Version=12 / ApiKey=sk-v6</c> → <c>Version=2 / ApiKey=sk-v0</c>, six acknowledged
/// writes destroyed while the write reported success.</para>
///
/// <para><b>Two-stage, so it costs nothing on the happy path.</b> A per-path in-process
/// high-water mark (fed by every write AND every read this process performs — no extra I/O)
/// is a cheap FILTER, not the verdict: a write at or above the mark goes straight through.
/// Only a SUSPECTED regression pays for a <see cref="IStorageAdapter.Read"/> of the current
/// durable row, and the verdict is taken against THAT. So a stale high-water mark (the node
/// was deleted and recreated by another replica, the store was restored out of band) can
/// never refuse a legitimate write — it only costs one extra read, after which the mark is
/// corrected.</para>
///
/// <para><b>Refuse, loudly — never throw.</b> A refusal logs at <c>Error</c> with both
/// versions and the path, and emits the STORED (winning) node so the caller's "what is
/// durable now" contract still holds. Throwing would turn a data-integrity save into a
/// faulted create/flush chain on paths that are not written to handle it (the create
/// pipeline, the dispose-time flush) — trading silent loss for a different outage.</para>
///
/// <para><b>Equal versions pass.</b> The guard rejects STRICT regressions only. A re-write at
/// the same version is a legitimate, common shape: static/never-mutated nodes sit at their
/// seed version forever, and content edits that don't route through a version-minting write
/// path re-persist at the same version.</para>
///
/// <para><b>Legitimate rewinds.</b> There is no framework path that writes a node backward:
/// version restore (<c>VersionPlugin.RestoreVersion</c>, <c>VersionLayoutArea</c>) re-stamps
/// the historical node with <c>Version = 0</c> so the owner mints a NEW top version, imports
/// and GitSync go through <c>CreateOrUpdateNodeRequest</c> → the owner's
/// <c>stream.Update</c> → <see cref="MeshNode.NextVersion"/>, and a delete drops the row (so
/// a recreate at <c>Version = 1</c> faces no stored row at all — see
/// <see cref="Delete"/>/<see cref="DeleteIfExists"/> forgetting the mark). Hence NO bypass
/// hatch is offered: adding an unused one would be a standing invitation to route around the
/// invariant. A future genuine rewind (a repair tool) must either delete-then-write or write
/// forward — the same rule every existing path already follows.</para>
///
/// <para><b>Footprint.</b> One <c>path → long</c> entry per node this process has written or
/// read, dropped on delete — the same per-path process-wide shape
/// <c>MeshNodeStreamCache</c> already carries, at a fraction of the size.</para>
/// </summary>
internal sealed class MonotonicWriteGuardStorageAdapter(
    IStorageAdapter inner,
    ILogger<MonotonicWriteGuardStorageAdapter>? logger = null) : IStorageAdapter
{
    private readonly ConcurrentDictionary<string, long> _highWater = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 🚨 Decorators MUST forward Changes — the interface default is
    /// <c>Observable.Empty</c>, and every synced query subscribed to
    /// <c>persistence.Changes</c> would silently stop receiving notifications
    /// (the failure mode <see cref="VersionWritingStorageAdapter"/> documents).
    /// </summary>
    public IObservable<DataChangeNotification> Changes => inner.Changes;

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => inner.Read(path, options).Do(Observe);

    /// <inheritdoc />
    public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => inner.ReadMany(paths, options).Do(Observe);

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
    {
        var path = node.Path;
        if (string.IsNullOrEmpty(path))
            return inner.Write(node, options);

        // Defer so the high-water probe runs at Subscribe (with the caller's I/O scope),
        // never at composition time.
        return Observable.Defer(() =>
        {
            if (!_highWater.TryGetValue(path, out var mark) || node.Version >= mark)
                return WriteAndRecord(node, options);

            // Suspected regression. The mark alone is NOT the verdict — verify against the
            // current durable row so a stale mark (cross-replica delete + recreate, an
            // out-of-band store restore) can never refuse a legitimate write.
            return inner.Read(path, options)
                .Take(1)
                .Catch<MeshNode?, Exception>(ex =>
                {
                    // Fail OPEN on an unreadable store: refusing a write because we could not
                    // read is how a guard turns a transient read fault into data loss of its own.
                    logger?.LogWarning(ex,
                        "[MonotonicWriteGuard] verification read failed for {Path} (incoming version {Version}, "
                        + "high-water {Mark}) — allowing the write rather than refusing on an unverified suspicion.",
                        path, node.Version, mark);
                    return Observable.Return<MeshNode?>(null);
                })
                .SelectMany(stored =>
                {
                    if (stored is null || stored.Version <= node.Version)
                    {
                        // The mark was STALE — the durable row is at or below the incoming
                        // version. Reset it to the verified truth (not Math.Max, which would
                        // keep the bogus high mark and make every later write for this path
                        // pay another verification read) and let the write through.
                        ResetMark(path, stored?.Version);
                        return WriteAndRecord(node, options);
                    }

                    // Confirmed backward write against live durable state. Refuse.
                    ResetMark(path, stored.Version);
                    logger?.LogError(
                        "[MonotonicWriteGuard] REFUSED a backward write to {Path}: incoming Version={IncomingVersion} "
                        + "is BELOW the stored Version={StoredVersion}. MeshNode.Version is the owner's monotonic "
                        + "persistence clock (MeshNode.NextVersion floors every mint at current+1), so a regressing "
                        + "write is a STALE snapshot about to destroy acknowledged data — not a newer state. The "
                        + "stored node is kept. Find the writer that adopted a stale own-node snapshot; do not relax "
                        + "this guard.",
                        path, node.Version, stored.Version);
                    return Observable.Return<MeshNode?>(stored);
                });
        });
    }

    private IObservable<MeshNode?> WriteAndRecord(MeshNode node, JsonSerializerOptions options)
        => inner.Write(node, options).Do(Observe);

    /// <summary>
    /// Raises the per-path high-water mark from any node this process saw as durable —
    /// a completed write or a read straight out of the store. A <c>null</c> emission
    /// means "no row" / "path not claimed by any provider" and records nothing.
    /// </summary>
    private void Observe(MeshNode? node)
    {
        if (node is null || string.IsNullOrEmpty(node.Path))
            return;
        var version = node.Version;
        _highWater.AddOrUpdate(node.Path, version, (_, current) => Math.Max(current, version));
    }

    /// <inheritdoc />
    public IObservable<string> Delete(string path)
        => inner.Delete(path).Do(_ => Forget(path));

    /// <inheritdoc />
    public IObservable<bool> DeleteIfExists(string path)
        => inner.DeleteIfExists(path).Do(removed =>
        {
            if (removed) Forget(path);
        });

    /// <summary>
    /// Snaps the mark to what a verification read just proved is durable — the one place the
    /// mark may move DOWN. Only reachable after that read, so it can never weaken the guard:
    /// a too-low mark merely costs one more verification read, and that read is the authority.
    /// A <c>null</c> version means "no stored row" and drops the entry entirely.
    /// </summary>
    private void ResetMark(string path, long? version)
    {
        if (version is { } v)
            _highWater[path] = v;
        else
            Forget(path);
    }

    /// <summary>
    /// Drops the mark for a deleted path so a same-path recreate (which legitimately
    /// restarts at <c>Version = 1</c>) is never mistaken for a regression.
    /// </summary>
    private void Forget(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            _highWater.TryRemove(path, out _);
    }

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => inner.ListChildPaths(parentPath);

    /// <inheritdoc />
    public IObservable<bool> Exists(string path) => inner.Exists(path);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => inner.FindBestPrefixMatch(fullPath, options);

    /// <inheritdoc />
    /// <remarks>
    /// Explicit forward for the same reason <see cref="VersionWritingStorageAdapter"/>
    /// forwards it: the interface default would route back through
    /// <c>this.FindBestPrefixMatch</c> and strip the Postgres satellite-UNION.
    /// </remarks>
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => inner.ResolvePath(fullPath, options);

    /// <inheritdoc />
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => inner.ListPartitionSubPaths(nodePath);

    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => inner.GetPartitionObjects(nodePath, subPath, options);

    /// <inheritdoc />
    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => inner.SavePartitionObjects(nodePath, subPath, objects, options);

    /// <inheritdoc />
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => inner.DeletePartitionObjects(nodePath, subPath);

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => inner.GetPartitionMaxTimestamp(nodePath, subPath);
}
