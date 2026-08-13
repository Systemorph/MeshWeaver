using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
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
/// <para>🚨 <b>The mark is CLAIMED BEFORE the row is mutated — never after (#826).</b> The filter
/// is only sound while the mark is at-or-above every version this process has already committed.
/// Recording it AFTER the inner write completed left a window in which the row already carried
/// the NEW version while the mark still advertised the OLD one — and that window is not
/// theoretical: every backend publishes <see cref="IStorageAdapter.Changes"/> from INSIDE the
/// write (<c>InMemoryStorageAdapter.Write</c>: <c>_nodes[path] = node</c> then
/// <c>_changes.OnNext(...)</c>), and the framework's own topology puts writers on that feed —
/// every per-node hub reconciles its own node from it
/// (<c>MeshDataSourceExtensions.SubscribeToOwnDeletion</c>). A stale snapshot presented in that
/// window carries a version at-or-below the STALE mark, so it skipped verification entirely and
/// OVERWROTE the newer row. The store was then genuinely behind, and the next write — minted one
/// above the rolled-back row — was accepted by this guard for the perfectly good reason that its
/// verification read confirmed the row really was older. That is the whole causal chain behind
/// the post-recycle write-rollback flake (<c>StaleActivationSeedRollbackTest</c>:
/// <c>Expected 2 to be greater than 7000</c>; <c>StaleActivationDurableFirstSeedTest</c>: an
/// ACKED advance to 9000 read back as <c>1</c>). Claiming first makes the window inert: a writer
/// that checks after our claim is verified against durable truth, and one that checks before it
/// commits BEFORE us and is overwritten by our (newer) row. Pinned by
/// <c>MonotonicWriteGuardWindowTest</c>. A claim left high by a write that then fails is
/// harmless by the paragraph above — a too-high mark only costs one verification read.</para>
///
/// <para>🚨 <b>The in-process mark is a FILTER, never the durable guarantee (#971).</b> A second
/// replica starts with an empty mark table, so its FIRST write to a path has nothing to compare
/// against and takes the fast path unverified. The durable half of the invariant therefore lives in
/// the STORE: every backend that can express the condition makes its upsert conditional on
/// <see cref="MeshNode.Version"/> (Postgres <c>ON CONFLICT … DO UPDATE … WHERE
/// target.version &lt;= EXCLUDED.version</c>, Snowflake's <c>WHEN MATCHED AND …</c>, the in-memory
/// store's version-keeping <c>AddOrUpdate</c>) and reports a refusal by emitting the STORED node
/// instead of the written one. This decorator treats that emission exactly like its own verification
/// read: as a confirmed conflict to resolve. Without it, monotonicity on a fresh replica's first
/// write was enforced by nothing at all — not the empty mark, not the unconditional upsert.</para>
///
/// <para><b>Resolve by merging — never refuse the caller (#971).</b> A confirmed conflict is NOT
/// bounced back to the writer: callers of <c>stream.Update</c> do not write conflict-retry loops and
/// must not have to. The durable row is re-read and the losing write is reconciled into it by
/// <see cref="MeshNodeConflictMerge"/> — non-strings latest-wins, strings merged where one side is a
/// superset, anything not auto-resolvable latest-wins. Nothing throws: turning a data-integrity save
/// into a faulted create/flush chain on paths not written to handle it (the create pipeline, the
/// dispose-time flush) would trade silent loss for a different outage.</para>
///
/// <para>🚨 <b>Latest-wins is acceptable; latest-wins INVISIBLY is the bug.</b> Every member the
/// merge could not auto-resolve is logged at <c>Warning</c> AND recorded as an <c>ActivityLog</c>
/// MeshNode satellite at <c>{path}/_Activity/write-conflict-{latestVersion}</c>. The defect this
/// component was built for (#826) was an acked write that rolled a row back with no error anywhere —
/// so a resolution that drops a value must leave a durable, user-visible trace. The id is keyed on the
/// DURABLE version alone, which bounds the record to one per revision no matter how many losing
/// attempts land against it (see <see cref="RecordConflictActivity"/>).</para>
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

                    // Confirmed backward write against live durable state.
                    ResetMark(path, stored.Version);
                    return ResolveConflict(node, stored, options);
                });
        });
    }

    /// <summary>
    /// Reconciles a write that lost the version race, and returns what is durable afterwards.
    ///
    /// <para>Reached from BOTH conflict detectors — this decorator's own verification read, and a
    /// store whose version-conditional upsert refused the write and handed back the row it kept.
    /// They are the same event, so they resolve the same way (#971).</para>
    ///
    /// <para>🚨 <b>One attempt, never a retry loop.</b> The merged node is written once. If the row
    /// moved again in between, the second refusal is LOGGED and the newer row is returned — a
    /// re-merge loop here would be an unbounded spin against a hot path, and the write it is spinning
    /// for is by construction older than what is already durable.</para>
    /// </summary>
    private IObservable<MeshNode?> ResolveConflict(MeshNode stale, MeshNode latest, JsonSerializerOptions options)
    {
        var path = latest.Path;
        var resolution = MeshNodeConflictMerge.Merge(latest, stale, options);

        logger?.LogWarning(
            "[MonotonicWriteGuard] CONFLICT on {Path}: a write at Version={IncomingVersion} lost the race against "
            + "the durable Version={StoredVersion}. MeshNode.Version is the owner's monotonic persistence clock "
            + "(MeshNode.NextVersion floors every mint at current+1), so the losing write is a STALE snapshot, not a "
            + "newer state. Resolved by merging into the durable row: merged={MergedMembers}; "
            + "latest-wins (stale values DROPPED)={OverwrittenMembers}. Find the writer that adopted a stale "
            + "own-node snapshot; do not relax this guard.",
            path, stale.Version, latest.Version,
            Describe(resolution.MergedMembers), Describe(resolution.OverwrittenMembers));

        RecordConflictActivity(path, stale.Version, latest.Version, resolution, options);

        if (resolution.IsLatestUnchanged)
            return Observable.Return<MeshNode?>(latest);   // nothing salvageable — the durable row already IS the answer

        Observe(resolution.Node);
        return inner.Write(resolution.Node, options)
            .Select(written =>
            {
                if (written is not null && written.Version > resolution.Node.Version)
                {
                    logger?.LogWarning(
                        "[MonotonicWriteGuard] the merged resolution for {Path} was itself refused — the row advanced "
                        + "to Version={StoredVersion} while the merge ran. Keeping the newer row; NOT re-merging (a "
                        + "retry loop here spins against a hot path for a write that is older than durable truth).",
                        path, written.Version);
                    return written;
                }
                return written ?? resolution.Node;
            });
    }

    private static string Describe(System.Collections.Immutable.ImmutableList<string> members)
        => members.IsEmpty ? "(none)" : string.Join(", ", members);

    /// <summary>
    /// Writes the durable, user-visible trace of a resolved conflict: an <c>ActivityLog</c> MeshNode
    /// satellite at <c>{path}/_Activity/write-conflict-{latestVersion}</c>, carrying one
    /// <see cref="LogLevel.Warning"/> message per member whose value was dropped (and an informational
    /// line for what was merged).
    ///
    /// <para>Best effort by contract — a failure to record the trace must never fail the resolution it
    /// describes, so it is logged rather than propagated. It is also skipped for paths already inside an
    /// <c>_Activity</c> namespace, so a conflict on a trace can never write a trace about a trace.</para>
    /// </summary>
    private void RecordConflictActivity(
        string path, long staleVersion, long latestVersion,
        MeshNodeConflictResolution resolution, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(path)
            || path.Contains("/_Activity/", StringComparison.OrdinalIgnoreCase))
            return;

        var messages = ImmutableList<LogMessage>.Empty
            .Add(new LogMessage(
                $"A write at Version={staleVersion} lost the race against the durable Version={latestVersion} "
                + $"on '{path}' and was merged into it.",
                LogLevel.Information));
        messages = resolution.OverwrittenMembers.Aggregate(messages, (acc, member) => acc.Add(
            new LogMessage(
                $"'{member}' was not auto-resolvable — the newer value was kept and the losing write's value dropped.",
                LogLevel.Warning)));
        messages = resolution.MergedMembers.Aggregate(messages, (acc, member) => acc.Add(
            new LogMessage($"'{member}' was merged — both writes' content survives.", LogLevel.Information)));

        // 🚨 The id is keyed on the DURABLE version alone, never on the losing writer's. One record
        // per durable revision is the right granularity ("this revision had a conflict") AND it is
        // what bounds the litter: a wedged owner mints an INCREASING version on every retry
        // (834, 835, 836 … against a stuck 2423 — the #725/#872 fork shape), so a stale-version-keyed
        // id would accumulate one satellite per retry, forever. Re-recording overwrites the same node
        // at Version = 1, which the store's equal-version rule accepts, so this can never conflict
        // with itself. Which losing version lost is in the message text.
        var activityNamespace = $"{path}/_Activity";
        var id = $"write-conflict-{latestVersion}";
        var activity = new MeshNode(id, activityNamespace)
        {
            Name = $"Write conflict on {path}",
            NodeType = "ActivityLog",
            MainNode = path,
            State = MeshNodeState.Active,
            Version = 1,
            LastModified = DateTimeOffset.UtcNow,
            Content = new ActivityLog(ActivityCategory.WriteConflict)
            {
                Id = id,
                HubPath = path,
                AffectedPaths = [path],
                End = DateTime.UtcNow,
                Status = resolution.OverwrittenMembers.IsEmpty
                    ? ActivityStatus.Succeeded
                    : ActivityStatus.Warning,
            }.Append(messages)
        };

        inner.Write(activity, options).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex,
                "[MonotonicWriteGuard] could not record the write-conflict activity for {Path}; the conflict itself "
                + "was resolved and is reported in the preceding warning.", path));
    }

    /// <summary>
    /// Claims the path's high-water mark for <paramref name="node"/> and THEN writes it.
    /// <para>🚨 The claim happens BEFORE <c>inner.Write</c> is subscribed — see the "claimed
    /// before the row is mutated" note on the class. Observing only the completed write left the
    /// mark trailing the row for the whole duration of that write (including the change-feed
    /// fan-out it performs from inside itself), and a stale writer landing in that window skipped
    /// verification and rolled the store back.</para>
    ///
    /// <para>🚨 A backend emission carrying a version ABOVE the one we handed it is the store
    /// reporting that its version-conditional upsert REFUSED the write and kept the row it already
    /// had (#971) — the cross-replica half of the invariant, and the only thing standing between a
    /// fresh replica's first write and a silent rollback. It is a confirmed conflict, so it resolves
    /// through exactly the same merge as the verification-read branch. The trailing
    /// <c>Do(Observe)</c> then tracks whatever is actually durable.</para>
    /// </summary>
    private IObservable<MeshNode?> WriteAndRecord(MeshNode node, JsonSerializerOptions options)
    {
        Observe(node);
        return inner.Write(node, options)
            .SelectMany(written => written is not null && written.Version > node.Version
                ? ResolveConflict(node, written, options)
                : Observable.Return(written))
            .Do(Observe);
    }

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
    /// <remarks>
    /// Explicit forward, and NO guard of its own: a compare-and-set is conditioned on the exact
    /// durable version the caller read, so by construction it cannot move the row backward — the
    /// store itself refuses when the row has advanced. Applying the high-water FILTER here would be
    /// strictly harmful, because the caller of a CAS wants the store's verdict, not a merge. The
    /// mark is still claimed on an APPLIED write so the ordinary <see cref="Write"/> path stays
    /// sound afterwards (the "claim before the row is mutated" rule cannot apply — the row's fate is
    /// decided inside the store — so we record what the store confirmed instead).
    /// </remarks>
    public IObservable<bool?> WriteIfVersion(
        MeshNode node, long expectedVersion, JsonSerializerOptions options)
        => inner.WriteIfVersion(node, expectedVersion, options)
            .Do(applied =>
            {
                if (applied is true) Observe(node);
            });

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
    /// <remarks>
    /// Explicit forward — the interface default would walk <c>this.ListChildPaths</c>
    /// level by level and strip the backend's native prefix enumeration (the Postgres
    /// satellite-UNION), the same trap <see cref="ResolvePath"/> documents.
    /// </remarks>
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => inner.ListDescendantPaths(rootPath);

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
