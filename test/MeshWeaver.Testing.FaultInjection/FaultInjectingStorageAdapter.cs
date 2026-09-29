using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Testing.FaultInjection;

/// <summary>
/// The INNERMOST storage decorator of a test mesh: it sits directly on the store of record, under
/// every guard the platform stacks on top, and injects three storage faults on demand, each as a
/// <see cref="FaultSwitch"/>:
///
/// <list type="bullet">
///   <item><see cref="HoldWrites"/> — the <b>flush hold</b>. A write of the path reaches the adapter
///     and waits until released, so the owning hub has committed a state in memory that storage does
///     not hold yet. That is the production window between an own-node commit and its flush, widened
///     from milliseconds to "until the test says so" (MeshWeaver#5670, Plugins#2536).</item>
///   <item><see cref="HidePath"/> — the <b>NotFound window</b>. Reads, existence probes and route
///     resolution answer as if the path did not exist yet (resolution stops at its parent), while
///     writes still land. It is the shape of a replica or index that has not caught up with a create
///     another process already acknowledged (Plugins#2530).</item>
///   <item><see cref="HoldChangeFeed"/> — the <b>post-storage invalidation hold</b>. Change
///     notifications are queued in order and delivered on release, which is a slow or reconnecting
///     LISTEN channel in production.</item>
/// </list>
///
/// <para>It is also the process's end of a <see cref="CrossProcessChangeRelay"/>: what the relay
/// delivers from other processes arrives on <see cref="Changes"/> exactly as a PostgreSQL NOTIFY does
/// on every replica.</para>
///
/// <para>Every <see cref="IStorageAdapter"/> member is declared and forwarded, including the ones with
/// default implementations — a decorator that inherits a default silently disables it for everything
/// below (see <c>StorageAdapterDecoratorsForwardBatchReadGuard</c>). Instance state only: it is a
/// singleton of one mesh (or one silo) and dies with it.</para>
/// </summary>
public sealed class FaultInjectingStorageAdapter : IStorageAdapter
{
    private readonly IStorageAdapter _inner;
    private readonly ConcurrentDictionary<string, FaultSwitch> _writeHolds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FaultSwitch> _hidden = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ReplaySubject<MeshNode>> _heldWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ReplaySubject<string>> _enumerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISubject<DataChangeNotification> _remote = Subject.Synchronize(new Subject<DataChangeNotification>());
    private FaultSwitch? _feedHold;

    /// <summary>Wraps <paramref name="inner"/>, the store of record.</summary>
    /// <param name="inner">The adapter that actually stores.</param>
    /// <param name="name">Which process this is ("silo 0"), for failure messages.</param>
    public FaultInjectingStorageAdapter(IStorageAdapter inner, string name = "mesh")
    {
        _inner = inner;
        Name = name;
        // The arrival is noted when the notification ARRIVES, not when its turn in the Concat
        // comes: a notification queued behind an earlier held one would otherwise be reported only
        // after the release, and a test waiting for "my path reached the hold" would wait forever.
        Changes = Observable.Merge(inner.Changes, _remote.AsObservable())
            .Select(n => Volatile.Read(ref _feedHold) is { IsClosed: true } hold
                ? Held(hold, n, $"change {n.Kind} {n.Path}")
                : Observable.Return(n))
            .Concat();
    }

    /// <summary>Which process this adapter belongs to.</summary>
    public string Name { get; }

    /// <summary>The adapter underneath, for tests that assert on what the store of record holds.</summary>
    public IStorageAdapter Inner => _inner;

    // ── The injectors ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// From now on, every write of <paramref name="path"/> reaches this adapter and waits until the
    /// returned switch is released. Each held write is reported on <see cref="HeldWrites"/>.
    /// </summary>
    /// <param name="path">The node path whose writes to hold.</param>
    public FaultSwitch HoldWrites(string path)
    {
        var key = Norm(path);
        return _writeHolds.GetOrAdd(key, k => new FaultSwitch($"{Name}: hold writes of {k}",
            s => _writeHolds.TryRemove(new KeyValuePair<string, FaultSwitch>(k, s))));
    }

    /// <summary>
    /// From now on, <paramref name="path"/> and its descendants read as absent: <c>Read</c> answers
    /// null, <c>Exists</c> false, route resolution stops at the parent, and the parent's child listing
    /// omits it. Writes still land. Each read the fault answered is an arrival on the switch.
    /// </summary>
    /// <param name="path">The node path to hide.</param>
    public FaultSwitch HidePath(string path)
    {
        var key = Norm(path);
        return _hidden.GetOrAdd(key, k => new FaultSwitch($"{Name}: hide {k}",
            s => _hidden.TryRemove(new KeyValuePair<string, FaultSwitch>(k, s))));
    }

    /// <summary>
    /// From now on, change notifications (local commits and relayed ones) queue in order and are
    /// delivered when the returned switch is released. One feed hold at a time.
    /// </summary>
    public FaultSwitch HoldChangeFeed()
    {
        var hold = new FaultSwitch($"{Name}: hold the change feed",
            s => Interlocked.CompareExchange(ref _feedHold, null, s));
        while (true)
        {
            var existing = Volatile.Read(ref _feedHold);
            if (existing is { IsClosed: true })
                throw new InvalidOperationException($"{existing.Name} is already in force.");
            // A released predecessor whose slot-clear has not run yet is replaced, never kept:
            // the returned switch is always the one the pipeline reads.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _feedHold, hold, existing), existing))
                return hold;
        }
    }

    /// <summary>Each write of <paramref name="path"/> that met a closed <see cref="HoldWrites"/> (replayed).</summary>
    /// <param name="path">The node path.</param>
    public IObservable<MeshNode> HeldWrites(string path) => HeldWriteSubject(Norm(path)).AsObservable();

    /// <summary>
    /// Each subtree enumeration of <paramref name="path"/> (<c>ListChildPaths</c> /
    /// <c>ListDescendantPaths</c>) made WHILE its writes were held, naming the call (replayed). Only a
    /// copy or a move enumerates a node's subtree, so this reads as "something relocated this node from
    /// storage while its latest commit was still in flight".
    /// </summary>
    /// <param name="path">The node path.</param>
    public IObservable<string> EnumeratedWhileHeld(string path) => EnumerationSubject(Norm(path)).AsObservable();

    /// <summary>The commits THIS process made, as its own backend reports them — what a relay forwards.</summary>
    public IObservable<DataChangeNotification> LocalCommits => _inner.Changes;

    /// <summary>
    /// Delivers a change another process committed, as a LISTEN/NOTIFY channel would. Subject to
    /// <see cref="HoldChangeFeed"/>.
    /// </summary>
    /// <param name="notification">The remote commit.</param>
    public void DeliverRemoteChange(DataChangeNotification notification) => _remote.OnNext(notification);

    // ── IStorageAdapter ──────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IObservable<DataChangeNotification> Changes { get; }

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => Observable.Defer(() => HiddenBy(path) is { } hidden
            ? Answered(hidden, $"Read {path}", (MeshNode?)null)
            : _inner.Read(path, options));

    /// <inheritdoc />
    public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            var visible = paths.Where(p =>
            {
                if (HiddenBy(p) is not { } hidden) return true;
                hidden.NoteArrival($"ReadMany {p}");
                return false;
            }).ToList();
            return visible.Count == 0 ? Observable.Empty<MeshNode>() : _inner.ReadMany(visible, options);
        });

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => HeldWrite(node, () => _inner.Write(node, options));

    /// <inheritdoc />
    public IObservable<IReadOnlyList<MeshNode>> WriteMany(IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            // One held node holds the batch: the batch is one flush, and it lands or waits as one.
            // Capture each switch ONCE: a release between two lookups would otherwise hand the
            // gate a switch that is no longer registered.
            var held = nodes.Select(n => (Node: n, Hold: WriteHold(n.Path)))
                .Where(h => h.Hold is not null).ToList();
            if (held.Count == 0 || held[0].Hold is not { } gate)
                return _inner.WriteMany(nodes, options);
            foreach (var (node, _) in held)
                HeldWriteSubject(Norm(node.Path)).OnNext(node);
            return gate.Gate(
                Observable.Defer(() => _inner.WriteMany(nodes, options)),
                $"WriteMany [{string.Join(", ", nodes.Select(n => n.Path))}]");
        });

    /// <inheritdoc />
    public IObservable<bool?> WriteIfVersion(MeshNode node, long expectedVersion, JsonSerializerOptions options)
        => HeldWrite(node, () => _inner.WriteIfVersion(node, expectedVersion, options));

    /// <inheritdoc />
    public IObservable<string> Delete(string path) => _inner.Delete(path);

    /// <inheritdoc />
    public IObservable<bool> DeleteIfExists(string path) => _inner.DeleteIfExists(path);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths) => _inner.DeleteMany(paths);

    /// <inheritdoc />
    public IObservable<string?> FindDeleteBlockingProvider(string path) => _inner.FindDeleteBlockingProvider(path);

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => Observable.Defer(() =>
        {
            NoteEnumeration(parentPath, nameof(ListChildPaths));
            return _inner.ListChildPaths(parentPath).Select(listing => (
                (IEnumerable<string>)listing.NodePaths.Where(p => Visible(p, $"ListChildPaths {parentPath}")).ToList(),
                (IEnumerable<string>)listing.DirectoryPaths.Where(p => Visible(p, $"ListChildPaths {parentPath}")).ToList()));
        });

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => Observable.Defer(() =>
        {
            NoteEnumeration(rootPath, nameof(ListDescendantPaths));
            return _inner.ListDescendantPaths(rootPath)
                .Select(set => (IReadOnlyCollection<string>)set.Where(p => Visible(p, $"ListDescendantPaths {rootPath}")).ToList());
        });

    /// <inheritdoc />
    public IObservable<bool> Exists(string path)
        => Observable.Defer(() => HiddenBy(path) is { } hidden
            ? Answered(hidden, $"Exists {path}", false)
            : _inner.Exists(path));

    /// <inheritdoc />
    public IObservable<bool> ExistsInWritableStorage(string path)
        => Observable.Defer(() => HiddenBy(path) is { } hidden
            ? Answered(hidden, $"ExistsInWritableStorage {path}", false)
            : _inner.ExistsInWritableStorage(path));

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(string fullPath, JsonSerializerOptions options)
        => Observable.Defer(() => HiddenRoot(fullPath) is { } root
            ? Resolved(root, $"FindBestPrefixMatch {fullPath}", r => _inner.FindBestPrefixMatch(r, options))
            : _inner.FindBestPrefixMatch(fullPath, options));

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(string fullPath, JsonSerializerOptions options)
        => Observable.Defer(() => HiddenRoot(fullPath) is { } root
            ? Resolved(root, $"ResolvePath {fullPath}", r => _inner.ResolvePath(r, options))
            : _inner.ResolvePath(fullPath, options));

    /// <inheritdoc />
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath) => _inner.ListPartitionSubPaths(nodePath);

    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => _inner.GetPartitionObjects(nodePath, subPath, options);

    /// <inheritdoc />
    public IObservable<Unit> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => _inner.SavePartitionObjects(nodePath, subPath, objects, options);

    /// <inheritdoc />
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => _inner.DeletePartitionObjects(nodePath, subPath);

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => _inner.GetPartitionMaxTimestamp(nodePath, subPath);

    // ── Mechanics ────────────────────────────────────────────────────────────────────────────────

    private static string Norm(string? path) => path?.Trim('/') ?? "";

    private static IObservable<T> Held<T>(FaultSwitch hold, T value, string what)
    {
        hold.NoteArrival(what);
        return hold.Released.Select(_ => value);
    }

    private static IObservable<T> Answered<T>(FaultSwitch fault, string what, T answer)
    {
        fault.NoteArrival(what);
        return Observable.Return(answer);
    }

    private static IObservable<(MeshNode? Node, int MatchedSegments)> Resolved(
        (FaultSwitch Fault, string Parent) root, string what,
        Func<string, IObservable<(MeshNode? Node, int MatchedSegments)>> resolve)
    {
        root.Fault.NoteArrival(what);
        return root.Parent.Length == 0
            ? Observable.Return(((MeshNode?)null, 0))
            : resolve(root.Parent);
    }

    private FaultSwitch? WriteHold(string? path)
        => _writeHolds.TryGetValue(Norm(path), out var hold) && hold.IsClosed ? hold : null;

    private IObservable<T> HeldWrite<T>(MeshNode node, Func<IObservable<T>> write)
        => Observable.Defer(() =>
        {
            if (WriteHold(node.Path) is not { } hold)
                return write();
            HeldWriteSubject(Norm(node.Path)).OnNext(node);
            return hold.Gate(Observable.Defer(write), $"Write {node.Path} v{node.Version}");
        });

    private FaultSwitch? HiddenBy(string? path) => HiddenRoot(path)?.Fault;

    /// <summary>False — and an arrival on the hide — when a listing would reveal a hidden path.</summary>
    private bool Visible(string? path, string what)
    {
        if (HiddenBy(path) is not { } hidden)
            return true;
        hidden.NoteArrival($"{what} (omits {Norm(path)})");
        return false;
    }

    /// <summary>The closed hide covering <paramref name="path"/> (itself or an ancestor) and that root's parent.</summary>
    private (FaultSwitch Fault, string Parent)? HiddenRoot(string? path)
    {
        if (_hidden.IsEmpty)
            return null;
        var p = Norm(path);
        foreach (var (root, fault) in _hidden)
        {
            if (!fault.IsClosed)
                continue;
            if (p.Equals(root, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                var slash = root.LastIndexOf('/');
                return (fault, slash < 0 ? "" : root[..slash]);
            }
        }
        return null;
    }

    private void NoteEnumeration(string? path, string call)
    {
        var key = Norm(path);
        if (_writeHolds.TryGetValue(key, out var hold) && hold.IsClosed)
            EnumerationSubject(key).OnNext(call);
    }

    private ReplaySubject<MeshNode> HeldWriteSubject(string key)
        => _heldWrites.GetOrAdd(key, _ => new ReplaySubject<MeshNode>());

    private ReplaySubject<string> EnumerationSubject(string key)
        => _enumerations.GetOrAdd(key, _ => new ReplaySubject<string>());
}
