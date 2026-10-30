using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Mesh-scoped durable tombstone for just-deleted node paths — the "delete wins" guard against the
/// resurrect-after-delete write race.
///
/// <para>When a node is deleted, a per-node hub that (re)activates AFTER the delete gets a STALE
/// own-node snapshot from the routing catalog stream's <c>Replay(1)</c> buffer. Its per-node
/// TypeSource workspace then sees that snapshot as an "add" and queues a debounced save that
/// RE-PERSISTS (resurrects) the deleted row — the intermittent <c>SpaceDeletionPartitionDropTests</c>
/// flake (a save lands on the deleted path ~200 ms after the delete, so every later read correctly
/// sees a live row).</para>
///
/// <para>Population is <b>synchronous, at the delete source</b>: <c>HandleDeleteNodeRequest</c> marks
/// every path it deletes here BEFORE it returns its response — so the tombstone is in place before the
/// deleting call completes, and therefore before any later hub can activate and resurrect the row.
/// This is what makes the guard deterministic: an earlier attempt that populated only from the async
/// per-hub <c>storage.Changes</c> subscriber still raced at cold start (no per-node hub was active yet
/// to observe the delete). Per-node hubs (via the Graph MeshDataSource guards) then READ this registry
/// and drop a resurrecting save; a legitimate re-create <see cref="Supersede"/>s the tombstone.</para>
///
/// <para><b>Superseding is SYNCHRONOUS at the durable write, exactly as marking is synchronous at the
/// delete (#3008).</b> The storage write seam (the outermost <c>IStorageAdapter</c> decorator) calls
/// <see cref="Supersede"/> on the write's post-commit emission — strictly BEFORE the caller's
/// <c>IMeshChangeFeed</c> <c>Created</c> publish, which is composed downstream of that emission. Until
/// this existed the only clear ran inside a LIVE per-node hub's change handler, i.e. asynchronously,
/// incidentally (it needed a hub to be alive for the path at that moment) and conditionally (a
/// same-version recreate was skipped as a self-write echo) — so a reader whose delivery landed on the
/// still-disposing old hub was NACKed with the authoritative "the node was deleted, so this address
/// will not reactivate" for a node that provably existed again, and the reader stopped, by contract.</para>
///
/// <para>A superseded tombstone answers <see cref="IsRecentlyDeleted"/> / <see cref="IsDeleted"/> with
/// <c>false</c> — the address is not gone for good and a save to it is not a resurrection — but the
/// delete stays ON RECORD for the TTL, together with the version the recreate landed at: that is what
/// lets <c>MeshNodeTypeSource</c> tell a legitimate version rewind (a recreate restarting at
/// <c>Version = 1</c>) from a stale replay (<see cref="IsRecreatedAt"/>). Erasing the entry at the write
/// would have thrown that recognition away for a hub whose own-node stream delivers the recreate later.</para>
///
/// <para>Instance-only state (a <see cref="ConcurrentDictionary{TKey,TValue}"/> — the lifetime is the
/// mesh singleton's, never <c>static</c>; see NoStaticState.md). TTL-bounded so the map can't grow
/// unbounded; superseded entries expire on the same TTL.</para>
///
/// <para>It is also the mesh's <see cref="IAddressTombstones"/>: the message pipeline reads it to
/// tell "this hub is going down because its node was DELETED" (the address is gone for good) from
/// "this hub is recycling / restarting" (it will reactivate), which decides whether an abandoned
/// delivery is NACKed as an authoritative NotFound or as the transient
/// <c>ErrorType.ShuttingDown</c>. See <see cref="IAddressTombstones"/> for why that distinction is
/// load-bearing.</para>
/// </summary>
public sealed class RecentlyDeletedRegistry : IAddressTombstones, IActiveDeletionScopes
{
    // Long enough to cover the full delete → (deactivate) → re-activate → debounced-flush window
    // under CI load (the resurrecting save fires ~200 ms after the delete; the guard must outlive
    // the slowest activation), short enough that a stale entry only ever blocks one save that would
    // itself be a no-op. A re-create clears the entry explicitly, so the TTL is only a backstop.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One delete on record. <see cref="SupersededAtVersion"/> is <c>null</c> while the tombstone is
    /// LIVE (the address is gone for good); once a durable write lands on the path it carries the
    /// version that write committed at — the LOWEST such version, i.e. the recreate itself.
    /// </summary>
    private readonly record struct Tombstone(DateTimeOffset DeletedAt, long? SupersededAtVersion);

    private readonly ConcurrentDictionary<string, Tombstone> _deleted =
        new(StringComparer.OrdinalIgnoreCase);

    // UtcTicks of the last opportunistic prune sweep — gates the O(n) sweep in MarkDeleted to at
    // most once per TTL so a delete burst stays amortised O(1) while the map stays TTL-bounded even
    // for tombstones that are never re-checked (IsRecentlyDeleted only prunes the key it looks up).
    private long _lastPruneTicks;

    /// <summary>Records <paramref name="path"/> as just-deleted. Called synchronously from the delete
    /// handler for every deleted path. Opportunistically prunes expired tombstones (time-gated) so a
    /// delete that is never re-read afterwards doesn't leak an entry forever.</summary>
    public void MarkDeleted(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        var now = DateTimeOffset.UtcNow;
        _deleted[path] = new Tombstone(now, SupersededAtVersion: null);

        // Time-gated sweep: only one thread prunes per TTL window (CAS on _lastPruneTicks), so the
        // map can't accumulate tombstones for one-off deletes that are never checked/cleared again.
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now.UtcTicks - last > Ttl.Ticks
            && Interlocked.CompareExchange(ref _lastPruneTicks, now.UtcTicks, last) == last)
        {
            foreach (var kv in _deleted)
                if (now - kv.Value.DeletedAt > Ttl)
                    _deleted.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>Removes the tombstone for <paramref name="path"/> outright. Prefer
    /// <see cref="Supersede"/> for a (re)create — it keeps the delete on record so a version rewind
    /// is still recognised as the recreate; <c>Clear</c> forgets that too.</summary>
    public void Clear(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            _deleted.TryRemove(path, out _);
    }

    /// <summary>
    /// Records that a durable write landed on <paramref name="path"/> at <paramref name="version"/>
    /// AFTER its delete — the tombstone is superseded: the address is no longer "gone for good"
    /// (<see cref="IsDeleted"/> / <see cref="IsRecentlyDeleted"/> turn <c>false</c>) while the delete
    /// stays on record for the TTL so <see cref="IsRecreatedAt"/> can recognise the recreate.
    ///
    /// <para>Called synchronously by the storage write seam on the write's post-commit emission, and
    /// by a live per-node hub's change handler when it adopts a persisted (re)create. Untracked paths
    /// (the overwhelming majority of writes) cost one dictionary probe and nothing else; a tombstone
    /// already superseded keeps the LOWER version — the first write after the delete IS the recreate,
    /// every later one is an ordinary update above it.</para>
    /// </summary>
    public void Supersede(string? path, long version)
    {
        if (string.IsNullOrEmpty(path) || _deleted.IsEmpty)
            return;
        var now = DateTimeOffset.UtcNow;
        while (_deleted.TryGetValue(path, out var current))
        {
            // An expired tombstone is already dead for every reader (TryGetLive prunes it on
            // access) — prune it here too rather than stamp a recreate version onto a corpse.
            if (now - current.DeletedAt > Ttl)
            {
                _deleted.TryRemove(new KeyValuePair<string, Tombstone>(path, current));
                return;
            }
            if (current.SupersededAtVersion is { } already && already <= version)
                return;
            if (_deleted.TryUpdate(path, current with { SupersededAtVersion = version }, current))
                return;
        }
    }

    /// <summary>True when <paramref name="path"/> was deleted within the TTL window and has not
    /// been re-created since — the caller (a per-node hub's save path) must then DROP the write.
    /// Expired entries are pruned on access; a superseded entry answers <c>false</c>.</summary>
    public bool IsRecentlyDeleted(string? path)
        => TryGetLive(path, out var tombstone) && tombstone.SupersededAtVersion is null;

    /// <summary>
    /// True when <paramref name="path"/> was deleted within the TTL window and a durable write has
    /// since landed on it at or below <paramref name="version"/> — i.e. an own-node emission carrying
    /// <paramref name="version"/> IS the recreate (or a later update of it), not a stale replay of the
    /// pre-delete node. The version floor in <c>MeshNodeTypeSource</c> resets on this instead of
    /// dropping the emission as a regression.
    /// </summary>
    public bool IsRecreatedAt(string? path, long version)
        => TryGetLive(path, out var tombstone)
           && tombstone.SupersededAtVersion is { } recreatedAt
           && version >= recreatedAt;

    private bool TryGetLive(string? path, out Tombstone tombstone)
    {
        tombstone = default;
        if (string.IsNullOrEmpty(path) || !_deleted.TryGetValue(path, out tombstone))
            return false;
        if (DateTimeOffset.UtcNow - tombstone.DeletedAt > Ttl)
        {
            _deleted.TryRemove(path, out _);
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>The pipeline-facing reading of the tombstone: live (<see cref="IsRecentlyDeleted"/>) AND no
    /// write to <paramref name="path"/> admitted to the store and still in flight. Kept as a separate
    /// member so the message pipeline depends on the intent (<see cref="IAddressTombstones"/> — "is this
    /// address gone for good?") rather than on this class, which lives above it in the reference graph.</para>
    /// <para>🚨 <b>Why an in-flight write already answers "not gone"</b> (seen as
    /// <c>InboxOpenApprovalsTest.APreferencesNodeDeletedAndRecreated_NeverEndsTheFeed</c> timing out;
    /// pinned by <c>ARecreateRetiresTheReadOpenedOnTheDeletedNodeTest</c>). The
    /// tombstone is superseded on the write's post-commit EMISSION (<see cref="Supersede"/>, called by the
    /// storage write guard), but the store publishes the row on its own change feed INSIDE the commit,
    /// before that emission. Synced queries are fed by that feed, so a reader that follows the CQRS
    /// rule — a listing for existence, then the node's stream for content — sees the recreated row
    /// and opens its read while the tombstone is still live. The read's request reaches the old,
    /// still-disposing hub (routing short-circuits on a hosted address), which asks this method and
    /// answers the authoritative, non-retryable "the node was deleted, so this address will not
    /// reactivate" for a node the store already holds again. The reader stops, by contract, and
    /// nothing ever tells it otherwise. A write the guard ADMITTED (<see cref="AdmitWrite"/>) is
    /// registered before it reaches the store and stays registered until it terminates, i.e. past the
    /// supersede — so from the moment a recreate can be visible to anyone until the tombstone is
    /// superseded, the dying hub answers the transient "shutting down" instead, which the reader rides
    /// out until the new activation answers. A write that then FAILS restores the verdict as soon as it
    /// unregisters; the cost is that a delivery racing that failed write is told "retry" and finds the
    /// routing NotFound on its re-probe. The resurrection guard (<see cref="IsRecentlyDeleted"/>) is
    /// unchanged: an in-flight write does not license a resurrecting save.</para>
    /// </remarks>
    public bool IsDeleted(string? path)
        // 🚨 The admission snapshot FIRST, the tombstone second. A recreate supersedes the tombstone
        // before it releases its admission, so the reverse order could read the live tombstone, then
        // — the recreate having committed and settled in between — no admission, and call a node the
        // store holds again "gone for good". In this order a write that settled before the admission
        // read has already superseded the tombstone the second read sees.
        => !string.IsNullOrEmpty(path) && !HasAdmittedWriteAt(path) && IsRecentlyDeleted(path);

    /// <summary>
    /// True when a write the storage guard admitted to exactly <paramref name="path"/> has not settled —
    /// see <see cref="IsDeleted"/>. A snapshot read, like <see cref="HasAdmittedWriteUnder"/>.
    /// </summary>
    private bool HasAdmittedWriteAt(string path) => HasAdmittedWriteUnder(path, rowOnly: true);

    // ─── Active subtree deletions ────────────────────────────────────────────
    //
    // Unlike the TTL tombstones above (a backstop against the resurrect-save
    // race), an ACTIVE subtree deletion is a hard invariant with an explicit
    // lifetime: HandleDeleteNodeRequest opens a scope BEFORE it enumerates the
    // deletion plan and closes it when the operation completes (success OR
    // failure — the scope rides an Observable.Using, so error/timeout/unsubscribe
    // all release it). While the scope is open, the storage write guard
    // (SubtreeDeletionGuardStorageAdapter) refuses every write at or under the
    // root — so a node created mid-delete (e.g. a compile-watcher Release
    // satellite) cannot land under a subtree that is being torn down. No timer,
    // no TTL: the invariant holds exactly as long as the delete is in flight.

    private readonly ConcurrentDictionary<string, int> _activeSubtreeDeletions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks <paramref name="rootPath"/>'s subtree as being deleted. Ref-counted, so two
    /// concurrent deletes of the same root (double-click) each hold the scope independently.
    /// Dispose the returned scope when the delete operation completes — success or failure.
    /// </summary>
    public IDisposable BeginSubtreeDeletion(string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath))
            return EmptyScope.Instance;
        _activeSubtreeDeletions.AddOrUpdate(rootPath, 1, (_, count) => count + 1);
        return new SubtreeDeletionScope(this, rootPath);
    }

    // Row-only deletions (DeleteNodeRequest.KeepDescendants): the same hard invariant as a
    // subtree deletion, for the ONE path being removed — writes under it are not guarded,
    // because the operation leaves them in place.
    private readonly ConcurrentDictionary<string, int> _activeRowDeletions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks the node at <paramref name="path"/> — and only that node, never what lies under it —
    /// as being deleted, for a row-only delete (<c>DeleteNodeRequest.KeepDescendants</c>). While the
    /// scope is open the storage write guard refuses a write AT <paramref name="path"/>, so the
    /// delete's version precondition still holds at its commit; writes under the path proceed.
    /// Ref-counted like <see cref="BeginSubtreeDeletion"/>; dispose when the delete completes.
    /// </summary>
    /// <param name="path">The node being deleted alone.</param>
    public IDisposable BeginRowDeletion(string path)
    {
        if (string.IsNullOrEmpty(path))
            return EmptyScope.Instance;
        _activeRowDeletions.AddOrUpdate(path, 1, (_, count) => count + 1);
        return new SubtreeDeletionScope(this, path, rowOnly: true);
    }

    /// <summary>
    /// Runs <paramref name="body"/> inside a <see cref="BeginSubtreeDeletion"/> scope on
    /// <paramref name="rootPath"/>, and RELEASES the scope BEFORE anything the body produced reaches
    /// the subscriber — its values, its completion and its error alike. Cold: the scope opens on
    /// Subscribe, before the body is subscribed.
    ///
    /// <para>🚨 <b>Why not <c>Observable.Using</c> alone.</b> Rx disposes a <c>Using</c> resource only
    /// once the subscription is torn down, which is AFTER the result has propagated downstream. The
    /// result is the "done" signal, so a caller that acts on it — a follow-up teardown of the same
    /// partition, a create under the path just moved away — runs while the scope is still held, and
    /// is refused as if the operation it was waiting for were still in flight ("a deletion … is
    /// already in flight — one teardown at a time"). <c>HandleDeleteNodeRequest</c> releases its scope
    /// by hand before posting its response for the same reason; this is that ordering as one
    /// primitive. The body's notifications are held until it terminates, the scope is released, and
    /// they are then delivered in order. <c>Using</c> stays underneath for unsubscribe, and the scope's
    /// Dispose is idempotent, so the early release and the safety net are one decrement.</para>
    /// </summary>
    /// <typeparam name="T">The body's element type.</typeparam>
    /// <param name="rootPath">The subtree being deleted (or moved away, or torn down).</param>
    /// <param name="body">The work the scope protects. Subscribed once, inside the scope.</param>
    /// <returns>The body's notifications, delivered after the scope is released.</returns>
    public IObservable<T> WithinSubtreeDeletion<T>(string rootPath, Func<IObservable<T>> body) =>
        WithinSubtreeDeletion(rootPath, new MeshOperationOptions().Timeout, body);

    /// <summary>
    /// <see cref="WithinSubtreeDeletion{T}(string, Func{IObservable{T}})"/> with an explicit bound on
    /// the wait for writes admitted under <paramref name="rootPath"/> before the scope opened (#6719).
    /// 🚨 The wait holds the scope open, and the scope refuses EVERY write under the root — so an
    /// admitted write that never terminates must not hold it forever. Past
    /// <paramref name="settleWithin"/> the operation fails with a <see cref="TimeoutException"/> naming
    /// the pending paths, the body never runs (nothing is torn down or moved), and the scope is
    /// released before the error is delivered.
    /// </summary>
    /// <typeparam name="T">The body's element type.</typeparam>
    /// <param name="rootPath">The subtree being deleted (or moved away, or torn down).</param>
    /// <param name="settleWithin">How long the admitted writes may take to settle.</param>
    /// <param name="body">The work the scope protects. Subscribed once, inside the scope.</param>
    /// <returns>The body's notifications, delivered after the scope is released.</returns>
    public IObservable<T> WithinSubtreeDeletion<T>(
        string rootPath, TimeSpan settleWithin, Func<IObservable<T>> body) =>
        Observable.Using(
            () => BeginSubtreeDeletion(rootPath),
            // The body runs only once every write ADMITTED under the root before the scope opened
            // has settled (#6719) — see AdmittedWritesSettled — and not at all if they do not.
            scope => AdmittedWritesSettled(rootPath)
                .Timeout(settleWithin, Observable.Defer(() => Observable.Throw<System.Reactive.Unit>(
                    new TimeoutException(
                        $"write(s) admitted under '{rootPath}' before its deletion scope opened did not "
                        + $"settle within {settleWithin.TotalSeconds:0}s: "
                        + $"{string.Join(", ", AdmittedWritesUnder(rootPath).Take(10))}. Nothing was done "
                        + "under the scope."))))
                .SelectMany(_ => Observable.Defer(body))
                .Materialize()
                .ToList()
                .Do(_ => scope.Dispose())
                .SelectMany(notifications => notifications.ToObservable(ImmediateScheduler.Instance))
                .Dematerialize());

    /// <summary>
    /// True when <paramref name="path"/> equals — or lies under — a subtree root whose
    /// deletion is currently in flight. <paramref name="deletionRoot"/> carries the matching
    /// root for diagnostics. The active-set is empty outside delete operations, so the scan
    /// is O(active deletes), i.e. effectively free on the write hot path.
    /// </summary>
    public bool IsUnderActiveDeletion(string? path, out string? deletionRoot)
    {
        deletionRoot = null;
        if (string.IsNullOrEmpty(path))
            return false;
        if (!_activeRowDeletions.IsEmpty && _activeRowDeletions.ContainsKey(path))
        {
            deletionRoot = path;
            return true;
        }
        if (_activeSubtreeDeletions.IsEmpty)
            return false;
        foreach (var kv in _activeSubtreeDeletions)
        {
            var root = kv.Key;
            if (path.Length < root.Length
                || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            if (path.Length == root.Length || path[root.Length] == '/')
            {
                deletionRoot = root;
                return true;
            }
        }
        return false;
    }

    // ─── Admitted writes (#6719) ─────────────────────────────────────────────
    //
    // The scope above refuses a write at ADMISSION. A write admitted a moment
    // before the scope opened is not refused: it is already on its way to the
    // store (queued on the I/O pool, or slow at the backend), and it commits
    // whenever the backend gets to it. Nothing ordered that commit against the
    // delete, so it could land AFTER the delete removed the row and recreate the
    // node the delete had just reported gone; its post-commit Supersede then
    // erased the tombstone, so the recreate looked legitimate.
    //
    // The ordering is closed from the delete's side: the storage write guard
    // REGISTERS every write it admits until that write terminates, and a
    // deletion, once its scope is open, waits for every write admitted under its
    // root to settle before it reads, plans or removes anything. The scope stops
    // new admissions; the wait drains the ones already admitted. Every in-process
    // write under the root is therefore either refused or committed BEFORE the
    // delete — never after it.
    //
    // Why both sides check in the order they do: the guard registers FIRST and
    // then tests the scope; the delete opens the scope FIRST and then reads the
    // registrations. Both are ConcurrentDictionary writes (full fences), so at
    // least one side sees the other — the write is refused, or the delete waits
    // for it. A write that registers and is then refused unregisters at once.

    private readonly ConcurrentDictionary<long, IReadOnlyCollection<string>> _admittedWrites = new();
    private long _nextAdmittedWrite;

    // Signals that an admitted write settled. Synchronized: writes settle on whatever thread the
    // backend completes them on, and a Subject's observers must not be called concurrently.
    private readonly ISubject<System.Reactive.Unit> _admittedWriteSettled =
        Subject.Synchronize(new Subject<System.Reactive.Unit>());

    /// <summary>
    /// Registers a write the storage write guard has ADMITTED, for the paths it writes. Dispose the
    /// returned handle when the write ITSELF terminates — completion or error; a caller unsubscribing does
    /// not stop an eager store write, so it is not a settlement — and
    /// any deletion waiting in <see cref="AdmittedWritesSettled"/> re-checks. Register BEFORE testing
    /// <see cref="IsUnderActiveDeletion"/>, never after: that order is what makes the two sides see
    /// each other (see the section comment).
    /// </summary>
    /// <param name="paths">The node paths the admitted write will commit.</param>
    /// <returns>The registration; disposing it twice is harmless.</returns>
    public IDisposable AdmitWrite(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
            return EmptyScope.Instance;
        var id = Interlocked.Increment(ref _nextAdmittedWrite);
        _admittedWrites[id] = paths;
        return new AdmittedWrite(this, id);
    }

    /// <summary>
    /// The paths of every write admitted at or under <paramref name="rootPath"/> that has not settled
    /// yet. Diagnostics for a deletion that is waiting on them.
    /// </summary>
    /// <param name="rootPath">The subtree root.</param>
    /// <param name="rowOnly">Only writes to <paramref name="rootPath"/> ITSELF — a row-only delete
    /// (<c>KeepDescendants</c>) removes nothing beneath it, so a descendant write is not its concern.</param>
    /// <returns>The pending paths, possibly empty.</returns>
    public IReadOnlyList<string> AdmittedWritesUnder(string rootPath, bool rowOnly = false)
        => _admittedWrites.Values
            .SelectMany(paths => paths)
            .Where(path => Covers(path, rootPath, rowOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Emits once, and completes, when no write admitted at or under <paramref name="rootPath"/> is
    /// still in flight. Subscribe to it only while the deletion scope on <paramref name="rootPath"/>
    /// is OPEN: the scope is what stops new admissions, so the set can only shrink and the wait
    /// terminates as soon as the writes already on their way have committed or failed. Cold; emits
    /// synchronously on Subscribe when nothing is pending, which is the overwhelmingly common case.
    /// </summary>
    /// <param name="rootPath">The subtree whose deletion is about to run.</param>
    /// <param name="rowOnly">Wait only for writes to <paramref name="rootPath"/> ITSELF. A row-only
    /// delete (<c>KeepDescendants</c>) guards just the row (<see cref="BeginRowDeletion"/>), so writes to
    /// the descendants it keeps stay admitted throughout and the pending set under the root need not
    /// shrink: waiting on them would time out a delete that has no pending write to the row it removes.</param>
    /// <returns>One <see cref="System.Reactive.Unit"/> once the subtree (or the row) has no admitted write pending.</returns>
    public IObservable<System.Reactive.Unit> AdmittedWritesSettled(string rootPath, bool rowOnly = false)
        => string.IsNullOrEmpty(rootPath)
            ? Observable.Return(System.Reactive.Unit.Default)
            // The settle signal is subscribed BEFORE the first check (Merge subscribes in order and
            // Return emits synchronously), so a write settling between the check and the
            // subscription cannot be missed.
            : Observable.Defer(() => _admittedWriteSettled
                .Merge(Observable.Return(System.Reactive.Unit.Default))
                .Where(_ => !HasAdmittedWriteUnder(rootPath, rowOnly))
                .Take(1));

    private bool HasAdmittedWriteUnder(string rootPath, bool rowOnly)
    {
        if (_admittedWrites.IsEmpty)
            return false;
        // 🚨 An atomic SNAPSHOT (Values takes every bucket lock), never the live enumerator (#7346
        // review). A write admitted at its owner's commit lets a write to the same path through an
        // open scope, so a NEW registration can be added under the root while an older one is
        // released. The live enumerator is not a snapshot: it could pass the new entry's bucket
        // before the add and reach the old one's after the remove, see neither, and let the deletion
        // run while the newer write was still on its way to the store. The new entry is always
        // added before the old one is removed, so a snapshot holds at least one of them.
        foreach (var paths in _admittedWrites.Values)
            foreach (var path in paths)
                if (Covers(path, rootPath, rowOnly))
                    return true;
        return false;
    }

    private static bool Covers(string path, string root, bool rowOnly)
        => rowOnly
            ? string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            : IsAtOrUnder(path, root);

    private static bool IsAtOrUnder(string path, string root)
        => path.Length >= root.Length
           && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
           && (path.Length == root.Length || path[root.Length] == '/');

    // ─── Writes admitted at their COMMIT (#7346) ──────────────────────────────
    //
    // The storage guard registers a write only when the write REACHES it. A per-node hub, though,
    // checks the scopes (IActiveDeletionScopes) in its merge turn, commits, and stores the write only
    // afterwards, on the commit echo. A delete opening its scope between the owner's check and the
    // storage write found nothing registered, ran, and the write's flush was then refused — by the
    // scope, or by the tombstone once the delete had answered — after the owner had committed and
    // served it: the writer heard "failed" for a write that had happened, the #6953 disagreement.
    //
    // So the owner's check IS an admission, held from the commit to the storage write's answer: it
    // is registered with the admitted writes (a deletion waits for it, exactly as for one the guard
    // admitted) and, while it is held, the guard lets a write to its path through an open scope —
    // the deletion is waiting for it, so it lands BEFORE the deletion, never after. The same
    // register-then-test order as the guard's (see the section above) makes the two sides see each
    // other: the write is refused at commit, or the deletion waits for it.
    //
    // By PATH, not by write: the commit and the storage write are different objects on different
    // threads. A second write to the same path that reaches the guard while an admission is held
    // passes too — and is registered by the guard, so the deletion waits for it as well. No NEW
    // admission is granted under an open scope, so the set only shrinks while a deletion waits.

    private ImmutableDictionary<long, string> _committedAdmissions = ImmutableDictionary<long, string>.Empty;

    /// <inheritdoc />
    public IDisposable? AdmitCommit(string? path, out string? deletionRoot)
    {
        deletionRoot = null;
        if (string.IsNullOrEmpty(path))
            return EmptyScope.Instance;
        // Register FIRST, then test — the order that makes a deletion opening concurrently either
        // refuse this write here or wait for it.
        var id = Interlocked.Increment(ref _nextAdmittedWrite);
        _admittedWrites[id] = [path];
        if (IsUnderActiveDeletion(path, out deletionRoot))
        {
            SettleAdmittedWrite(id);
            return null;
        }
        // 🚨 Published as a guard BYPASS only once the scope check has ACCEPTED the commit. Published
        // before it, a refused commit's transient entry let another write to the same path through
        // an open scope whose deletion had already finished waiting — and that write could land
        // after the deletion and recreate the row. The bypass is never needed earlier: the write it
        // exists for reaches the guard only after this commit, i.e. after this method returns.
        ImmutableInterlocked.Update(ref _committedAdmissions, set => set.SetItem(id, path));
        return new AdmittedWrite(this, id);
    }

    /// <summary>
    /// True while a write to exactly <paramref name="path"/> holds an admission taken at its commit
    /// (<see cref="AdmitCommit"/>) — the storage guard then lets it through a scope that opened after
    /// the admission, because that deletion is waiting for it.
    /// </summary>
    /// <param name="path">The path a write reaching the storage guard writes.</param>
    /// <returns><c>true</c> when the write was admitted at its commit and must not be refused.</returns>
    public bool IsAdmittedAtCommit(string? path)
    {
        var admissions = Volatile.Read(ref _committedAdmissions);
        if (admissions.IsEmpty || string.IsNullOrEmpty(path))
            return false;
        foreach (var admitted in admissions.Values)
            if (string.Equals(admitted, path, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void SettleAdmittedWrite(long id)
    {
        if (!Volatile.Read(ref _committedAdmissions).IsEmpty)
            ImmutableInterlocked.Update(ref _committedAdmissions, set => set.Remove(id));
        if (_admittedWrites.TryRemove(id, out _))
            _admittedWriteSettled.OnNext(System.Reactive.Unit.Default);
    }

    private sealed class AdmittedWrite(RecentlyDeletedRegistry registry, long id) : IDisposable
    {
        public void Dispose() => registry.SettleAdmittedWrite(id);
    }

    // ── Version-guarded deletes (#6673) ─────────────────────────────────────────────────────────
    //
    // A delete that names DeleteNodeRequest.ExpectedVersion asks the owning hub to compare that
    // version with the node's LIVE version inside the owner's commit turn. That comparison is only
    // worth something if no OWN write can be acknowledged after it: an own write (UpdateOwn) is
    // acknowledged from the in-memory commit, about one persistence-sampler interval BEFORE it is
    // stored, so a write acked after the comparison would be removed by the delete that the
    // comparison let through. This set is the seal that closes that window. The delete opens it
    // BEFORE it asks the owner to compare, and every own commit turn on the sealed path is refused
    // while it is open — so each own write either committed before the comparison (and the
    // comparison sees its version) or is refused in its turn (and nothing was acknowledged).
    // Exact path only: the precondition is the ROOT's, never a descendant's.

    private readonly ConcurrentDictionary<string, int> _versionGuardedDeletions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Seals <paramref name="path"/> against OWN writes for the duration of a version-guarded delete
    /// (<see cref="DeleteNodeRequest.ExpectedVersion"/>, #6673). Ref-counted like
    /// <see cref="BeginSubtreeDeletion"/>. Dispose the returned scope when the delete completes —
    /// success or failure.
    /// </summary>
    /// <param name="path">The exact path of the node the guarded delete targets.</param>
    /// <returns>The scope; disposing it lifts the seal (idempotent).</returns>
    public IDisposable BeginVersionGuardedDeletion(string path)
    {
        if (string.IsNullOrEmpty(path))
            return EmptyScope.Instance;
        _versionGuardedDeletions.AddOrUpdate(path, 1, (_, count) => count + 1);
        // Lifting a deleter-side seal announces itself like an owner seal's release, so an own write
        // the seal DEFERRED re-runs its commit turn (see VersionGuardLifted).
        return new CountedScope(_versionGuardedDeletions, path,
            () => _versionGuardReleased.OnNext(new VersionGuardRelease(path, DeleteSealOutcome.Lifted)));
    }

    // ── The OWNER's seal (#6856) — a SERIALIZED lane ───────────────────────────────────────────
    //
    // The seal above is taken by the DELETING process. When the owning activation runs in another
    // process (another silo), its own commit turn reads ITS process's registry, which the deleter
    // never touched. So the owner takes a seal of its own, in the commit turn that confirms the
    // version, and holds it until the deleter's commit or abort arrives (ReleaseDeleteSeal), or the
    // owning activation is torn down (then a store fence decides). No timer anywhere.
    //
    // 🚨 EVERY transition of an owner seal — take, release (commit / abort / indeterminate),
    // abort-before-check mark, teardown, fence start, fence completion, fence retry, settle, remove —
    // is ONE command on ONE serialized lane (a Subject + Select(Run) + Concat, the hub-serialization
    // pattern), so no two transitions can interleave. State is immutable and REPLACED by a command;
    // the commit turns that defer own writes only READ the published snapshot. The fence's store I/O
    // runs OUTSIDE the lane, on the I/O pool, and its completion comes back as a command naming its
    // attempt, so a stale attempt can never settle its replacement. Side effects a command decides
    // (guard disposal aside) — events, the teardown binding, starting a fence, answering a waiter —
    // run AFTER the command, outside the lane, so a subscriber can never re-enter it mid-transition.
    //
    // The lane is the process's (this mesh's), not the owning activation's: a seal must outlive the
    // activation that took it — its teardown is exactly when the fence runs.

    private readonly ISubject<Action> _sealLane = Subject.Synchronize(new Subject<Action>());
    private readonly ISubject<VersionGuardRelease> _versionGuardReleased =
        Subject.Synchronize(new Subject<VersionGuardRelease>());

    // Mutated ONLY inside a lane command. Published for the lock-free readers below.
    private ImmutableDictionary<string, OwnerSeal> _ownerSeals =
        ImmutableDictionary<string, OwnerSeal>.Empty.WithComparers(StringComparer.Ordinal);
    private ImmutableDictionary<string, int> _ownerSealedPaths =
        ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable _sealLaneSubscription;
    private int _fenceAttempts;

    /// <summary>Creates the registry and starts its seal lane.</summary>
    public RecentlyDeletedRegistry()
    {
        // LONG-LIVED: a command that faults is reported by its caller (Run rethrows), never ends the lane.
        _sealLaneSubscription = _sealLane
            .Select(command => Observable.Defer(() =>
            {
                command();
                return Observable.Return(System.Reactive.Unit.Default);
            }).Catch(Observable.Return(System.Reactive.Unit.Default)))
            .Concat()
            .Subscribe(_ => { }, _ => { });
    }

    /// <summary>
    /// Runs one seal transition on the lane and returns its result, then runs the side effects it
    /// decided — after the transition, outside the lane. The lane is synchronized and every command is
    /// synchronous, so the command has run when this returns.
    /// </summary>
    private T Run<T>(Func<SealStep<T>> command)
    {
        SealStep<T>? step = null;
        Exception? fault = null;
        _sealLane.OnNext(() =>
        {
            try { step = command(); }
            catch (Exception ex) { fault = ex; }
        });
        if (fault is not null)
            throw new InvalidOperationException("an owner-seal transition faulted", fault);
        if (step is null)
            throw new InvalidOperationException(
                "an owner-seal transition did not run inline — the lane was re-entered from inside a transition");
        foreach (var effect in step.Value.After)
            effect();
        return step.Value.Result;
    }

    private readonly record struct SealStep<T>(T Result, ImmutableList<Action> After)
    {
        public static SealStep<T> Of(T result, params Action[] after) => new(result, after.ToImmutableList());
    }

    private void Publish(ImmutableDictionary<string, OwnerSeal> seals)
    {
        _ownerSeals = seals;
        var sealedPaths = ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
        foreach (var seal in seals.Values)
            if (seal.Phase is not SealPhase.AbortMark)
                sealedPaths = sealedPaths.SetItem(seal.Path, sealedPaths.GetValueOrDefault(seal.Path) + 1);
        Volatile.Write(ref _ownerSealedPaths, sealedPaths);
    }

    /// <summary>
    /// Takes the OWNER's seal of <paramref name="path"/> for the version-guarded delete
    /// <paramref name="sealId"/> (#6856). Call it from the owner's commit turn that confirmed the
    /// version, so no own write commits between the comparison and the seal. While it is held the
    /// owner's own writes to <paramref name="path"/> are deferred, and the storage write guard of this
    /// process refuses writes of the row (a cross-hub patch's durable flush among them). Taking the same
    /// <paramref name="sealId"/> twice is one seal.
    /// <para>Refused (<c>false</c>) when the delete's ABORT reached this owner before the comparison
    /// did (an abort mark), and when the activation that took this seal was torn down and is fencing —
    /// the fence answers that delete.</para>
    /// </summary>
    /// <param name="sealId">The delete's seal id (<see cref="ValidateDeleteRequest.SealId"/>).</param>
    /// <param name="path">The node the delete targets.</param>
    /// <param name="bindToOwnerLifetime">Couples a release to the owning activation's lifetime and
    /// returns a handle that detaches it — the hub's <c>RegisterForDisposalDetachable</c>.</param>
    /// <param name="fenceOnTeardown">The STORE FENCE: a compare-and-set that moves the stored row past
    /// the confirmed version, so a delete that may still be on its way is refused at its
    /// compare-and-delete. It is handed a predicate that turns true once the delete's own commit or
    /// abort has arrived (it then stops before its next write), and emits <c>true</c> when it found the
    /// writable row already gone.</param>
    /// <returns><c>true</c> when the seal is held for <paramref name="sealId"/>.</returns>
    public bool TakeOwnerDeleteSeal(string sealId, string path, Func<IDisposable, IDisposable> bindToOwnerLifetime,
        Func<Func<bool>, IObservable<bool>>? fenceOnTeardown = null)
    {
        if (string.IsNullOrEmpty(sealId) || string.IsNullOrEmpty(path))
            return false;
        return Run(() =>
        {
            if (_ownerSeals.TryGetValue(sealId, out var existing))
            {
                if (existing.Phase is not SealPhase.AbortMark)
                    return SealStep<bool>.Of(existing.Phase is SealPhase.Held);
                // The abort arrived first: consume its mark — the delete is over.
                Publish(_ownerSeals.Remove(sealId));
                return SealStep<bool>.Of(false, existing.Teardown.Dispose);
            }
            var seal = new OwnerSeal(path, SealPhase.Held, BeginRowDeletion(path), fenceOnTeardown);
            Publish(_ownerSeals.SetItem(sealId, seal));
            // Bound after the transition: a teardown already under way releases at once, and that
            // release is the next command — it finds the seal.
            return SealStep<bool>.Of(true, () => seal.Teardown.Disposable = bindToOwnerLifetime(
                System.Reactive.Disposables.Disposable.Create(
                    () => ReleaseOwnerDeleteSeal(sealId, path, DeleteSealOutcome.OwnerTeardown))));
        });
    }

    /// <summary>
    /// Releases the owner's seal <paramref name="sealId"/> (#6856): on the deleter's commit, abort or
    /// indeterminate message, or from the owning activation's teardown.
    /// <list type="bullet">
    /// <item><description><see cref="DeleteSealOutcome.Committed"/>: the path is tombstoned BEFORE the
    /// seal lifts, so nothing deferred or pending stores the deleted node again; deferred writes are
    /// refused.</description></item>
    /// <item><description><see cref="DeleteSealOutcome.Aborted"/>: the seal lifts; deferred writes run.</description></item>
    /// <item><description><see cref="DeleteSealOutcome.Indeterminate"/> (the delete gave up while its
    /// storage delete may still be running) and <see cref="DeleteSealOutcome.OwnerTeardown"/>: the store
    /// fence decides; the path stays sealed until it has.</description></item>
    /// </list>
    /// <para>An unknown id is a no-op — unless <paramref name="markAbortOnOwner"/> is given and the
    /// outcome is <see cref="DeleteSealOutcome.Aborted"/>: the abort overtook the comparison, and an abort
    /// MARK bound to the owning activation's lifetime makes a later comparison refuse the seal.</para>
    /// </summary>
    /// <param name="sealId">The delete's seal id.</param>
    /// <param name="path">The node the seal covers; a release naming another path is ignored.</param>
    /// <param name="outcome">A <see cref="DeleteSealOutcome"/> value.</param>
    /// <param name="markAbortOnOwner">The owning activation's <c>RegisterForDisposalDetachable</c>, when
    /// the release is handled by the owner itself.</param>
    /// <returns><c>true</c> when this call reached a held or fencing seal.</returns>
    public bool ReleaseOwnerDeleteSeal(string sealId, string path, string outcome,
        Func<IDisposable, IDisposable>? markAbortOnOwner = null)
    {
        // The outcome is an OPEN wire vocabulary: an unknown value releases nothing.
        if (string.IsNullOrEmpty(sealId) || !IsKnownSealOutcome(outcome))
            return false;
        return Run(() =>
        {
            if (!_ownerSeals.TryGetValue(sealId, out var seal))
            {
                if (markAbortOnOwner is null
                    || !string.Equals(outcome, DeleteSealOutcome.Aborted, StringComparison.Ordinal))
                    return SealStep<bool>.Of(false);
                var mark = new OwnerSeal(path, SealPhase.AbortMark, null, null);
                Publish(_ownerSeals.SetItem(sealId, mark));
                // Lives exactly as long as the activation that could still run the comparison.
                return SealStep<bool>.Of(false, () => mark.Teardown.Disposable = markAbortOnOwner(
                    System.Reactive.Disposables.Disposable.Create(() => RemoveAbortMark(sealId, mark))));
            }
            if (seal.Phase is SealPhase.AbortMark
                || !string.Equals(seal.Path, path, StringComparison.OrdinalIgnoreCase))
                return SealStep<bool>.Of(false);

            var teardown = string.Equals(outcome, DeleteSealOutcome.OwnerTeardown, StringComparison.Ordinal);
            var indeterminate = string.Equals(outcome, DeleteSealOutcome.Indeterminate, StringComparison.Ordinal);
            if (seal.Phase is SealPhase.Held)
            {
                if ((teardown || indeterminate) && seal.Fence is not null)
                    return BeginFence(sealId, seal, teardown);
                return Settle(sealId, seal with { Terminal = teardown ? null : outcome }, rowGone: false,
                    detach: !teardown, emit: outcome);
            }
            // Fencing or fence failed. 🚨 A teardown of an owner whose fence FAILED (an indeterminate
            // delete's fence, run while it lived) retries the fence now: the fence carries the
            // confirmed state, which can hold an acknowledged write whose own save the seal refused —
            // and this activation's dispose flush cannot write past the row guard. Otherwise a
            // teardown changes nothing; a verdict is recorded and the fence stops before its next
            // write. Committed tombstones NOW — before anything can lift.
            if (teardown && seal.Phase is SealPhase.FenceFailed && seal.Terminal is null)
            {
                var retry = seal with { Phase = SealPhase.Fencing, Attempt = Interlocked.Increment(ref _fenceAttempts) };
                Publish(_ownerSeals.SetItem(sealId, retry));
                return SealStep<bool>.Of(true, () => StartFence(sealId, retry));
            }
            if (teardown || indeterminate || seal.Terminal is not null)
                return SealStep<bool>.Of(true);
            var decided = seal with { Terminal = outcome };
            if (string.Equals(outcome, DeleteSealOutcome.Committed, StringComparison.Ordinal))
                MarkDeleted(seal.Path);
            if (seal.Phase is SealPhase.FenceFailed)
                return Settle(sealId, decided, rowGone: false, detach: true, emit: outcome);
            Publish(_ownerSeals.SetItem(sealId, decided));
            return SealStep<bool>.Of(true);
        });
    }

    private void RemoveAbortMark(string sealId, OwnerSeal mark)
        => Run(() =>
        {
            if (_ownerSeals.TryGetValue(sealId, out var current) && ReferenceEquals(current, mark))
                Publish(_ownerSeals.Remove(sealId));
            return SealStep<bool>.Of(true);
        });

    /// <summary>
    /// The store fence starts (a command). 🚨 BOTH guards stay until the seal settles: the version
    /// guard (every own write in this process, a replacement activation's included, is deferred) and
    /// the ROW guard (every other write of the row — a cross-hub patch's durable flush among them — is
    /// refused at the storage guard). Only the fence's own compare-and-set passes that guard, admitted
    /// by identity (<see cref="AdmitFenceWrite"/>). On teardown the dying activation's deferred writes
    /// are refused at once (they were never acknowledged).
    /// </summary>
    private SealStep<bool> BeginFence(string sealId, OwnerSeal seal, bool teardown)
    {
        var attempt = Interlocked.Increment(ref _fenceAttempts);
        var fencing = seal with { Phase = SealPhase.Fencing, Attempt = attempt };
        Publish(_ownerSeals.SetItem(sealId, fencing));
        var effects = ImmutableList<Action>.Empty;
        if (teardown)
            effects = effects.Add(() => _versionGuardReleased.OnNext(
                new VersionGuardRelease(seal.Path, DeleteSealOutcome.OwnerTeardown)));
        effects = effects.Add(() => StartFence(sealId, fencing));
        return new SealStep<bool>(true, effects);
    }

    /// <summary>Runs one fence attempt OUTSIDE the lane; its completion returns as a command.</summary>
    private void StartFence(string sealId, OwnerSeal fencing)
    {
        // Read-only: the published snapshot says whether the delete's own verdict has arrived since.
        bool Stop() => !_ownerSeals.TryGetValue(sealId, out var now)
                       || now.Attempt != fencing.Attempt
                       || now.Terminal is not null;
        IObservable<bool> run;
        try
        {
            run = fencing.Fence!(Stop);
        }
        catch (Exception)
        {
            FenceCompleted(sealId, fencing.Attempt, rowGone: null);
            return;
        }
        run.Take(1).DefaultIfEmpty(false).Subscribe(
            rowGone => FenceCompleted(sealId, fencing.Attempt, rowGone),
            // A fault proves nothing about the row: reported to the lane as such.
            _ => FenceCompleted(sealId, fencing.Attempt, rowGone: null));
    }

    /// <summary>
    /// A fence attempt finished (a command). An attempt that is no longer the seal's current one is
    /// ignored — a stale callback can never settle its replacement. A FAULT (<paramref name="rowGone"/>
    /// null) settles nothing unless the delete's verdict already arrived: the path stays sealed, and the
    /// next own write the seal defers runs the fence again (<see cref="RetryFailedFence"/>).
    /// </summary>
    private void FenceCompleted(string sealId, int attempt, bool? rowGone)
        => Run(() =>
        {
            if (!_ownerSeals.TryGetValue(sealId, out var seal)
                || seal.Phase is not SealPhase.Fencing || seal.Attempt != attempt)
                return SealStep<bool>.Of(false);
            if (rowGone is { } gone)
                return Settle(sealId, seal, gone, detach: true,
                    emit: gone ? DeleteSealOutcome.Committed : seal.Terminal ?? DeleteSealOutcome.Lifted);
            if (seal.Terminal is not null)
                return Settle(sealId, seal, rowGone: false, detach: true, emit: seal.Terminal);
            Publish(_ownerSeals.SetItem(sealId, seal with { Phase = SealPhase.FenceFailed }));
            return SealStep<bool>.Of(false);
        });

    /// <summary>
    /// Settles a seal (inside a command): the tombstone first (a committed delete, or a fence that
    /// found the row gone), then the seal leaves the published state — so a reader that sees the path
    /// unsealed already sees it tombstoned. Events and waiters run after the command.
    /// </summary>
    private SealStep<bool> Settle(string sealId, OwnerSeal seal, bool rowGone, bool detach, string emit)
    {
        if (rowGone || string.Equals(seal.Terminal, DeleteSealOutcome.Committed, StringComparison.Ordinal))
            MarkDeleted(seal.Path);
        seal.RowGuard?.Dispose();
        Publish(_ownerSeals.Remove(sealId));
        var effects = ImmutableList<Action>.Empty;
        if (detach)
            effects = effects.Add(seal.Teardown.Dispose);
        effects = effects
            .Add(() => _versionGuardReleased.OnNext(new VersionGuardRelease(seal.Path, emit)))
            .Add(() =>
            {
                seal.Settled.OnNext(System.Reactive.Unit.Default);
                seal.Settled.OnCompleted();
            });
        return new SealStep<bool>(true, effects);
    }

    /// <summary>
    /// Runs the store fence again for every seal on <paramref name="path"/> whose fence FAILED (#6856).
    /// Called when an own write is deferred by such a seal: that write needs the answer.
    /// </summary>
    /// <param name="path">The sealed path.</param>
    public void RetryFailedFence(string path)
    {
        if (!Volatile.Read(ref _ownerSealedPaths).ContainsKey(path))
            return;
        Run(() =>
        {
            var effects = ImmutableList<Action>.Empty;
            var seals = _ownerSeals;
            foreach (var (sealId, seal) in _ownerSeals)
            {
                if (seal.Phase is not SealPhase.FenceFailed
                    || !string.Equals(seal.Path, path, StringComparison.OrdinalIgnoreCase))
                    continue;
                var retry = seal with { Phase = SealPhase.Fencing, Attempt = Interlocked.Increment(ref _fenceAttempts) };
                seals = seals.SetItem(sealId, retry);
                var id = sealId;
                effects = effects.Add(() => StartFence(id, retry));
            }
            Publish(seals);
            return new SealStep<bool>(true, effects);
        });
    }

    /// <summary>
    /// Emits once when the owner's seal <paramref name="sealId"/> has settled — at once when no such
    /// seal is held here. A commit for a seal that is fencing answers only after this, so the delete's
    /// success never reaches its caller while the fence could still write, nor before the tombstone.
    /// </summary>
    /// <param name="sealId">The delete's seal id.</param>
    /// <returns>One unit when the seal is settled.</returns>
    public IObservable<System.Reactive.Unit> OwnerSealSettled(string sealId)
        => Observable.Defer(() => _ownerSeals.TryGetValue(sealId, out var seal) && seal.Phase is not SealPhase.AbortMark
            ? seal.Settled.AsObservable()
            : Observable.Return(System.Reactive.Unit.Default));

    /// <summary>
    /// Emits once, the next time a version-guard seal on <paramref name="path"/> is lifted, with the
    /// <see cref="DeleteSealOutcome"/> it was lifted for — or at once with
    /// <see cref="DeleteSealOutcome.Lifted"/> when no seal holds it any more. An own write the seal
    /// deferred subscribes to this and then re-runs its commit turn, which checks the seal again: with
    /// two seals on the path, lifting one re-defers it. Subscribed to the releases BEFORE the state
    /// is read, so a release in between cannot be missed.
    /// </summary>
    /// <param name="path">The sealed path.</param>
    /// <returns>The outcome of the lift.</returns>
    public IObservable<string> VersionGuardLifted(string path)
        => VersionGuardReleases(path)
            .Merge(Observable.Defer(() => IsVersionGuardedDeletionInFlight(path)
                ? Observable.Never<string>()
                : Observable.Return(DeleteSealOutcome.Lifted)))
            .Take(1);

    /// <summary>
    /// Every lift of a version-guard seal on <paramref name="path"/>, with its
    /// <see cref="DeleteSealOutcome"/> — hot, no replay. The owner's persistence pipeline listens, so a
    /// save its row guard refused while sealed is re-dispatched when the delete aborts (#6856).
    /// </summary>
    /// <param name="path">The sealed path.</param>
    /// <returns>The outcome of each lift.</returns>
    public IObservable<string> VersionGuardReleases(string path)
        => _versionGuardReleased
            .Where(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Outcome);

    /// <summary>True for the <see cref="DeleteSealOutcome"/> values a seal can be released for.</summary>
    /// <param name="outcome">The outcome a release names.</param>
    /// <returns><c>true</c> for Committed, Aborted, Indeterminate and OwnerTeardown.</returns>
    public static bool IsKnownSealOutcome(string? outcome)
        => string.Equals(outcome, DeleteSealOutcome.Committed, StringComparison.Ordinal)
           || string.Equals(outcome, DeleteSealOutcome.Aborted, StringComparison.Ordinal)
           || string.Equals(outcome, DeleteSealOutcome.Indeterminate, StringComparison.Ordinal)
           || string.Equals(outcome, DeleteSealOutcome.OwnerTeardown, StringComparison.Ordinal);

    // ── The fence's own write, admitted past the row guard ─────────────────────────────────────
    private ImmutableHashSet<MeshNode> _admittedFenceWrites =
        ImmutableHashSet<MeshNode>.Empty.WithComparer(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Admits exactly <paramref name="fenceNode"/> — this instance, by reference — past the row guard
    /// an owner seal holds while its store fence runs (#6856). Every other write of the row stays
    /// refused. Dispose when the fence's compare-and-set has answered.
    /// </summary>
    /// <param name="fenceNode">The node instance the fence writes.</param>
    /// <returns>The admission; disposing it withdraws it.</returns>
    public IDisposable AdmitFenceWrite(MeshNode fenceNode)
    {
        ImmutableInterlocked.Update(ref _admittedFenceWrites, set => set.Add(fenceNode));
        return System.Reactive.Disposables.Disposable.Create(
            () => ImmutableInterlocked.Update(ref _admittedFenceWrites, set => set.Remove(fenceNode)));
    }

    /// <summary>True for the one write instance a running store fence is writing (#6856).</summary>
    /// <param name="node">The node a write carries.</param>
    /// <returns><c>true</c> when the storage guard must let it through.</returns>
    public bool IsAdmittedFenceWrite(MeshNode node)
        => !Volatile.Read(ref _admittedFenceWrites).IsEmpty && Volatile.Read(ref _admittedFenceWrites).Contains(node);

    // ── The deleter's side: what did its root storage delete answer? ───────────────────────────
    //
    // A guarded delete's root storage call is OBSERVED BY THE MESH, not by the delete's pipeline: the
    // pipeline's subscription can be cancelled (a commit-stage timeout) while the store keeps working,
    // so the store's real answer is recorded here, on its actual termination, and the pipeline only
    // subscribes to a replay. The release then asks this record — never "did my pipeline get as far as
    // noting the removal" — whether the delete removed the row, did not, or has not answered yet.

    private readonly ConcurrentDictionary<string, RootDeleteBook> _rootDeletes = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens the per-delete record of root storage calls for the delete <paramref name="operationKey"/>
    /// (its seal id), BEFORE the delete can issue any (#6856). Closed by
    /// <see cref="TakeRootDeleteVerdict"/>: a root delete that tries to issue after that is refused
    /// without touching the store, so no storage call can start after the delete's outcome was decided.
    /// </summary>
    /// <param name="operationKey">The delete's seal id.</param>
    public void OpenRootDeletes(string operationKey)
        => _rootDeletes.TryAdd(operationKey, new RootDeleteBook());

    /// <summary>
    /// Runs a guarded delete's root storage call under the mesh's own observation (#6856): it is
    /// subscribed ONCE, here, and runs to its real termination whatever the caller does; the answer is
    /// recorded for <see cref="TakeRootDeleteVerdict"/> and replayed to the caller. 🚨 Registered and
    /// issued only while the delete's record is OPEN: once its release has decided the outcome, the
    /// call is refused before it reaches the store.
    /// </summary>
    /// <param name="operationKey">The delete this call belongs to (its seal id) — never the path: two
    /// deletes of one path must not read, or consume, each other's answers.</param>
    /// <param name="storageCall">The cold compare-and-delete.</param>
    /// <returns>The store's answer, replayed — or an error when the delete was already decided.</returns>
    public IObservable<bool> ObserveRootDelete(string operationKey, IObservable<bool> storageCall)
    {
        var record = new RootDeleteRecord();
        if (!_rootDeletes.TryGetValue(operationKey, out var book) || !book.TryAdd(record))
            return Observable.Throw<bool>(new InvalidOperationException(
                $"the delete {operationKey} has already decided its outcome; its root storage delete was not issued"));
        var answer = new AsyncSubject<bool>();
        storageCall.Take(1).Subscribe(
            removed =>
            {
                record.Answer(removed ? RootDeleteVerdict.Removed : RootDeleteVerdict.NotRemoved);
                answer.OnNext(removed);
            },
            ex =>
            {
                // 🚨 An ERROR proves nothing about the row: a provider can remove a copy before
                // another faults, and an issued operation can outlive the aggregate error. Left
                // unanswered, so the delete's outcome is Indeterminate and the owner fences first.
                answer.OnError(ex);
            },
            () =>
            {
                record.Answer(RootDeleteVerdict.NotRemoved, onlyIfUnanswered: true);
                answer.OnCompleted();
            });
        return answer.AsObservable();
    }

    /// <summary>
    /// What one delete's guarded root storage calls answered, consumed by the
    /// delete's release (#6856): <see cref="RootDeleteVerdict.Removed"/> when any of them removed the
    /// row and none is still running, <see cref="RootDeleteVerdict.Unanswered"/> when ANY has not answered
    /// yet — or FAILED, which proves nothing about the row — even after an earlier pass removed it (its delete is
    /// <see cref="DeleteSealOutcome.Indeterminate"/>), else <see cref="RootDeleteVerdict.NotRemoved"/>
    /// (or none issued). Every record of the path is consumed: one still unanswered keeps running under
    /// the mesh's observation, but nothing of it is retained to misclassify a later delete.
    /// </summary>
    /// <param name="operationKey">The delete (its seal id) whose storage calls are asked.</param>
    /// <returns>The combined verdict.</returns>
    public RootDeleteVerdict TakeRootDeleteVerdict(string operationKey)
    {
        if (!_rootDeletes.TryRemove(operationKey, out var book))
            return RootDeleteVerdict.NotRemoved;
        // Closing and reading are one step: no root delete can register after this.
        var records = book.Close();
        // 🚨 An unanswered call DOMINATES: a removal in an earlier drain pass settles nothing about a
        // later root delete that is still running — it could still remove a node created after it.
        if (records.Any(r => r.MarkTaken() == RootDeleteVerdict.Unanswered))
            return RootDeleteVerdict.Unanswered;
        return records.Any(r => r.MarkTaken() == RootDeleteVerdict.Removed)
            ? RootDeleteVerdict.Removed
            : RootDeleteVerdict.NotRemoved;
    }

    /// <summary>The root storage calls of one delete; closed (null) once its outcome is decided.</summary>
    private sealed class RootDeleteBook
    {
        private ImmutableList<RootDeleteRecord>? _records = ImmutableList<RootDeleteRecord>.Empty;

        public bool TryAdd(RootDeleteRecord record)
        {
            while (true)
            {
                var current = Volatile.Read(ref _records);
                if (current is null)
                    return false;
                if (ReferenceEquals(Interlocked.CompareExchange(ref _records, current.Add(record), current), current))
                    return true;
            }
        }

        public ImmutableList<RootDeleteRecord> Close()
            => Interlocked.Exchange(ref _records, null) ?? ImmutableList<RootDeleteRecord>.Empty;
    }

    /// <summary>One root storage delete's answer.</summary>
    private sealed class RootDeleteRecord
    {
        private int _verdict = (int)RootDeleteVerdict.Unanswered;

        public void Answer(RootDeleteVerdict verdict, bool onlyIfUnanswered = false)
        {
            if (onlyIfUnanswered)
                Interlocked.CompareExchange(ref _verdict, (int)verdict, (int)RootDeleteVerdict.Unanswered);
            else
                Interlocked.Exchange(ref _verdict, (int)verdict);
        }

        /// <summary>The answer when the release took it.</summary>
        public RootDeleteVerdict MarkTaken() => (RootDeleteVerdict)Volatile.Read(ref _verdict);
    }

    private sealed record VersionGuardRelease(string Path, string Outcome);

    private enum SealPhase { Held, Fencing, FenceFailed, AbortMark }

    /// <summary>One owner seal — immutable; a lane command replaces it.</summary>
    private sealed record OwnerSeal(
        string Path, SealPhase Phase, IDisposable? RowGuard, Func<Func<bool>, IObservable<bool>>? Fence)
    {
        /// <summary>The delete's own verdict, once it arrived during a fence.</summary>
        public string? Terminal { get; init; }

        /// <summary>The fence attempt this state belongs to.</summary>
        public int Attempt { get; init; }

        /// <summary>Detaches the release bound to the owning activation's lifetime (shared by copies).</summary>
        public System.Reactive.Disposables.SingleAssignmentDisposable Teardown { get; init; } = new();

        /// <summary>Completes when the seal has settled (shared by copies).</summary>
        public AsyncSubject<System.Reactive.Unit> Settled { get; init; } = new();
    }

    /// <summary>
    /// True while a version-guarded delete of exactly <paramref name="path"/> is in flight. The
    /// owner's own-write commit turn reads this and refuses the write, so no own write is
    /// acknowledged between the guarded delete's version comparison and its commit.
    /// </summary>
    /// <param name="path">The path an own write is about to commit to.</param>
    /// <returns><c>true</c> when the path is sealed.</returns>
    public bool IsVersionGuardedDeletionInFlight(string? path)
        => !string.IsNullOrEmpty(path)
           && ((!_versionGuardedDeletions.IsEmpty && _versionGuardedDeletions.ContainsKey(path))
               || Volatile.Read(ref _ownerSealedPaths).ContainsKey(path));

    private sealed class CountedScope : IDisposable
    {
        private readonly ConcurrentDictionary<string, int> _counts;
        private readonly string _key;
        private readonly Action? _onDisposed;
        private int _disposed;

        public CountedScope(ConcurrentDictionary<string, int> counts, string key, Action? onDisposed)
        {
            _counts = counts;
            _key = key;
            _onDisposed = onDisposed;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Decrement();
            _onDisposed?.Invoke();
        }

        private void Decrement()
        {
            while (true)
            {
                if (!_counts.TryGetValue(_key, out var count))
                    return;
                if (count <= 1)
                {
                    if (_counts.TryRemove(new KeyValuePair<string, int>(_key, count)))
                        return;
                }
                else if (_counts.TryUpdate(_key, count - 1, count))
                    return;
            }
        }
    }

    /// <summary>
    /// Emits once, and completes, when <paramref name="path"/> is no longer at or under a deletion (or
    /// move) in flight — at once when it is not now. An owner DEFERS an own write to such a path until
    /// then (#6953), exactly as it defers one behind a version-guarded delete's seal
    /// (<see cref="VersionGuardLifted"/>): it re-runs the write, which the tombstone then refuses if the
    /// node went away and applies if the operation aborted. Driven by the same scope-end announcement
    /// <see cref="DeletionGuardsLifted"/> reads. Cold.
    /// </summary>
    /// <param name="path">The node path an own write waits to commit to.</param>
    /// <returns>One <see cref="System.Reactive.Unit"/> once no deletion scope covers the path.</returns>
    public IObservable<System.Reactive.Unit> ActiveDeletionLifted(string path)
        => Observable.Defer(() => _deletionScopeEnded
            .Select(_ => System.Reactive.Unit.Default)
            .Merge(Observable.Return(System.Reactive.Unit.Default))
            .Where(_ => !IsUnderActiveDeletion(path, out string? _))
            .Take(1));

    private void EndSubtreeDeletion(string rootPath, bool rowOnly)
    {
        var active = rowOnly ? _activeRowDeletions : _activeSubtreeDeletions;
        while (true)
        {
            if (!active.TryGetValue(rootPath, out var count))
                return;
            if (count <= 1)
            {
                if (active.TryRemove(
                        new KeyValuePair<string, int>(rootPath, count)))
                    break;
            }
            else if (active.TryUpdate(rootPath, count - 1, count))
                break;
        }
        // Announced, like a version guard's lift: a save a deletion guard refused may be waiting for
        // the LAST guard on its path to end (#6856, DeletionGuardsLifted).
        _deletionScopeEnded.OnNext(new DeletionScopeEnd(rootPath, rowOnly));
    }

    private sealed record DeletionScopeEnd(string Root, bool RowOnly);

    private readonly ISubject<DeletionScopeEnd> _deletionScopeEnded =
        Subject.Synchronize(new Subject<DeletionScopeEnd>());

    /// <summary>
    /// Emits each time the LAST deletion guard over <paramref name="path"/> may have ended (#6856): a
    /// version-guard seal lifted, or a subtree / row deletion scope covering the path ended — and, at
    /// that moment, no guard holds the path and it is not tombstoned. A save one of those guards
    /// refused is posted again on this: whichever guard ends last, its end is the signal, so the
    /// refused save is never stranded behind a guard whose end was not announced.
    /// </summary>
    /// <param name="path">The node path whose refused save waits.</param>
    /// <returns>A payload-free signal.</returns>
    public IObservable<System.Reactive.Unit> DeletionGuardsLifted(string path)
        => VersionGuardReleases(path)
            .Where(outcome => !string.Equals(outcome, DeleteSealOutcome.Committed, StringComparison.Ordinal)
                && !string.Equals(outcome, DeleteSealOutcome.OwnerTeardown, StringComparison.Ordinal))
            .Select(_ => System.Reactive.Unit.Default)
            .Merge(_deletionScopeEnded
                .Where(end => end.RowOnly
                    ? string.Equals(path, end.Root, StringComparison.OrdinalIgnoreCase)
                    : IsAtOrUnder(path, end.Root))
                .Select(_ => System.Reactive.Unit.Default))
            .Where(_ => !IsVersionGuardedDeletionInFlight(path)
                && !IsUnderActiveDeletion(path, out string? _)
                && !IsRecentlyDeleted(path));

    private sealed class SubtreeDeletionScope(
        RecentlyDeletedRegistry registry, string rootPath, bool rowOnly = false)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                registry.EndSubtreeDeletion(rootPath, rowOnly);
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// What a guarded delete's root storage call answered (#6856) — in-process only, never persisted or
/// serialized, and exhaustively handled by <see cref="RecentlyDeletedRegistry"/>'s callers.
/// </summary>
public enum RootDeleteVerdict
{
    /// <summary>The store has not answered yet (the call may still be running).</summary>
    Unanswered,

    /// <summary>The store removed the row.</summary>
    Removed,

    /// <summary>The store answered that it did not remove it (refused, or absent). A FAILED call is
    /// never this: it stays <see cref="Unanswered"/>.</summary>
    NotRemoved,
}
