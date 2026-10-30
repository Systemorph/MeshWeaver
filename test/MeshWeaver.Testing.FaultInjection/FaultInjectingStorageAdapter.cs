using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
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
///   <item><see cref="FailOperations(Func{string, bool}, StorageLegs, Func{string, Exception}?, int?, string?)"/>
///     — the <b>general operation fault</b>: the chosen legs (<see cref="StorageLegs"/>) of the paths a
///     predicate selects FAULT with the exception the test names, until released or for a fixed
///     number of applications. Read faults, write refusals, enumeration faults and one-shot failures
///     are all this one injector.</item>
///   <item><see cref="ServeReadOnlyFallback"/> — a <b>read-only provider under a writable
///     override</b>: reads of a path answer a shipped row whenever the store holds none.</item>
/// </list>
///
/// <para>Where it sits is the test's choice.
/// <see cref="FaultInjectionMeshExtensions.AddFaultInjectingStorage"/> puts it UNDER every platform
/// guard (the store of record); <see cref="FaultInjectionMeshExtensions.AddFaultInjectingStorageOverTheChain"/>
/// puts it OVER the whole production chain, where the caller's own read or write is the call that
/// faults; and a test that hands an adapter to the code under test directly constructs one over
/// whatever it wraps.</para>
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
    private readonly ConcurrentDictionary<string, FaultSwitch> _deleteHolds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FaultSwitch> _enumerationHolds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FaultSwitch> _conditionalWriteFaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FaultSwitch> _deleteManyFaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FaultSwitch> _deleteFaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FaultSwitch> _unreachableWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FaultSwitch> _judgedWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ReplaySubject<bool>> _conditionalDeleteResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ReplaySubject<MeshNode>> _heldWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ReplaySubject<string>> _enumerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (MeshNode Row, FaultSwitch Switch)> _readOnlyFallbacks = new(StringComparer.OrdinalIgnoreCase);
    private ImmutableList<OperationFault> _operationFaults = ImmutableList<OperationFault>.Empty;
    private readonly ISubject<DataChangeNotification> _remote = Subject.Synchronize(new Subject<DataChangeNotification>());
    private readonly ISubject<ChangeFeedGap> _gaps = Subject.Synchronize(new Subject<ChangeFeedGap>());
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
    /// From now on, every delete of <paramref name="path"/> that reaches this adapter (<c>Delete</c>,
    /// <c>DeleteIfExists</c>, <c>DeleteIfVersionAtMost</c>, a <c>DeleteMany</c> naming it) waits until the
    /// returned switch is released, and is then decided by the store underneath — atomically, as it
    /// would have been. It pauses a delete AFTER every check it made (the owner's verdict included) and
    /// before its commit lands (MeshWeaver#6856). Each held delete is an arrival on the switch.
    /// </summary>
    /// <param name="path">The node path whose deletes to hold.</param>
    public FaultSwitch HoldDeletes(string path)
    {
        var key = Norm(path);
        return _deleteHolds.GetOrAdd(key, k => new FaultSwitch($"{Name}: hold deletes of {k}",
            s => _deleteHolds.TryRemove(new KeyValuePair<string, FaultSwitch>(k, s))));
    }

    /// <summary>
    /// From now on, every compare-and-set (<c>WriteIfVersion</c>) of <paramref name="path"/> FAULTS
    /// until the returned switch is released — a store that cannot be reached for the conditional write
    /// (MeshWeaver#6856, the owner-teardown store fence). Each faulted call is an arrival on the switch.
    /// </summary>
    /// <param name="path">The node path whose conditional writes fault.</param>
    public FaultSwitch FailConditionalWrites(string path)
    {
        var key = Norm(path);
        return _conditionalWriteFaults.GetOrAdd(key, k => new FaultSwitch($"{Name}: fail conditional writes of {k}",
            s => _conditionalWriteFaults.TryRemove(new KeyValuePair<string, FaultSwitch>(k, s))));
    }

    /// <summary>
    /// From now on, every write of <paramref name="path"/> (<c>Write</c>, <c>WriteIfVersion</c>, a
    /// <c>WriteMany</c> naming it) FAULTS with a driver-shaped CONNECTION failure — a
    /// <see cref="System.Data.Common.DbException"/> with SQLSTATE <c>08006</c>, which is what a
    /// managed PostgreSQL restart answers (MeshWeaver#7068) — and writes nothing. The create
    /// handler classifies it as an unreachable store and answers <c>Unavailable</c>, exactly as
    /// production did (MeshWeaver#7072). Releasing the switch is the store coming back. Each refused
    /// write is an arrival on the switch.
    /// </summary>
    /// <param name="path">The node path whose writes cannot reach the store.</param>
    public FaultSwitch RefuseWritesAsUnreachable(string path)
    {
        var key = Norm(path);
        return _unreachableWrites.GetOrAdd(key, k => new FaultSwitch($"{Name}: store unreachable for writes of {k}",
            s => _unreachableWrites.TryRemove(new KeyValuePair<string, FaultSwitch>(k, s))));
    }

    /// <summary>
    /// From now on, every write of <paramref name="path"/> FAULTS with an
    /// <see cref="InvalidOperationException"/> — a store that was reached and REFUSED the write, which
    /// the create handler answers <c>ValidationFailed</c>: a verdict, not an outage. The counterpart of
    /// <see cref="RefuseWritesAsUnreachable"/>, for a case that needs the store to judge a write it
    /// earlier could not see (MeshWeaver#7087). Each refused write is an arrival on the switch.
    /// </summary>
    /// <param name="path">The node path whose writes the store refuses.</param>
    public FaultSwitch RefuseWritesAsJudged(string path)
    {
        var key = Norm(path);
        return _judgedWrites.GetOrAdd(key, k => new FaultSwitch($"{Name}: store refuses writes of {k}",
            s => _judgedWrites.TryRemove(new KeyValuePair<string, FaultSwitch>(k, s))));
    }

    /// <summary>
    /// From now on, a <c>DeleteMany</c> whose batch names <paramref name="path"/> deletes every OTHER
    /// path of the batch and then FAULTS, leaving <paramref name="path"/> stored — a batch delete that
    /// died part-way, as a sequential <c>DeleteMany</c> does when its store goes away mid-batch
    /// (MeshWeaver#6951: a move whose delete leg fails after its copy landed). The fault is a
    /// <see cref="BatchDeletePartiallyAppliedException"/> naming the rows that did leave storage, as
    /// the <c>DeleteMany</c> contract requires (MeshWeaver#7396). Each faulted batch is an arrival on
    /// the switch.
    /// </summary>
    /// <param name="path">The node path the batch stops at.</param>
    public FaultSwitch FailDeleteMany(string path)
    {
        var key = Norm(path);
        return _deleteManyFaults.GetOrAdd(key, k => new FaultSwitch($"{Name}: fail DeleteMany at {k}",
            s => _deleteManyFaults.TryRemove(new KeyValuePair<string, FaultSwitch>(k, s))));
    }

    /// <summary>
    /// From now on, a single-path <c>Delete</c> of <paramref name="path"/> FAULTS without deleting
    /// anything — the row stays stored. This is the rollback a create runs when a critical
    /// post-creation step failed (#638) dying on its store, so the create answers a failure over a
    /// row it DID write (MeshWeaver#6951: a copy must not read that failure as "nothing landed").
    /// Batch deletes (<c>DeleteMany</c>) are untouched. Each faulted delete is an arrival on the switch.
    /// </summary>
    /// <param name="path">The node path whose deletes fault.</param>
    public FaultSwitch FailDeletes(string path)
    {
        var key = Norm(path);
        return _deleteFaults.GetOrAdd(key, k => new FaultSwitch($"{Name}: fail deletes of {k}",
            s => _deleteFaults.TryRemove(new KeyValuePair<string, FaultSwitch>(k, s))));
    }

    /// <summary>
    /// From now on, every subtree enumeration of <paramref name="path"/> (<c>ListChildPaths</c> /
    /// <c>ListDescendantPaths</c>) waits until the returned switch is released. A delete plans its
    /// paths with one, AFTER the owner's version verdict, so this pauses a delete between that verdict
    /// and its has-children decision. Each held enumeration is an arrival on the switch.
    /// </summary>
    /// <param name="path">The node path whose enumerations to hold.</param>
    public FaultSwitch HoldEnumerations(string path)
    {
        var key = Norm(path);
        return _enumerationHolds.GetOrAdd(key, k => new FaultSwitch($"{Name}: hold enumerations of {k}",
            s => _enumerationHolds.TryRemove(new KeyValuePair<string, FaultSwitch>(k, s))));
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

    /// <summary>
    /// From now on, the <paramref name="legs"/> of every path <paramref name="matches"/> selects FAULT
    /// with <paramref name="error"/> and do nothing — the store underneath is never asked. A batch
    /// (<c>ReadMany</c>, <c>WriteMany</c>, <c>DeleteMany</c>) naming a selected path faults AS A WHOLE,
    /// which is the shape of one batched statement failing. Route resolution
    /// (<c>FindBestPrefixMatch</c> / <c>ResolvePath</c>), the partition-object surfaces and the change
    /// feed are never faulted by it; <see cref="HidePath"/> and <see cref="HoldChangeFeed"/> cover those.
    ///
    /// <para>Each faulted operation is an arrival on the returned switch, so
    /// <see cref="FaultSwitch.ArrivalCount"/> is the proof the fault was actually INJECTED. With
    /// <paramref name="times"/> the fault applies that many times and then releases itself — a write
    /// that fails exactly once and whose re-attempt lands is <c>times: 1</c>.</para>
    /// </summary>
    /// <param name="matches">Selects the faulted paths; it is handed the path with leading and
    /// trailing slashes trimmed.</param>
    /// <param name="legs">Which operations fault.</param>
    /// <param name="error">The exception for a faulted path, given that path; defaults to the
    /// driver's command-timeout shape, <c>TimeoutException("The operation has timed out.")</c>.</param>
    /// <param name="times">How many operations fault before the switch releases itself; <c>null</c>
    /// faults until released.</param>
    /// <param name="description">What the predicate selects, for the switch's name.</param>
    public FaultSwitch FailOperations(
        Func<string, bool> matches, StorageLegs legs, Func<string, Exception>? error = null,
        int? times = null, string? description = null)
    {
        var fault = new FaultSwitch($"{Name}: fail {legs} of {description ?? "the selected paths"}",
            s => ImmutableInterlocked.Update(ref _operationFaults,
                list => list.RemoveAll(f => ReferenceEquals(f.Switch, s))),
            times);
        var entry = new OperationFault(matches, legs,
            error ?? (_ => new TimeoutException("The operation has timed out.")), fault);
        ImmutableInterlocked.Update(ref _operationFaults, list => list.Add(entry));
        return fault;
    }

    /// <summary>
    /// <see cref="FailOperations(Func{string, bool}, StorageLegs, Func{string, Exception}?, int?, string?)"/>
    /// for exactly one path (compared ignoring case and surrounding slashes).
    /// </summary>
    /// <param name="path">The node path whose operations fault.</param>
    /// <param name="legs">Which operations fault.</param>
    /// <param name="error">The exception; defaults to the driver's command-timeout shape.</param>
    /// <param name="times">How many operations fault before the switch releases itself; <c>null</c>
    /// faults until released.</param>
    public FaultSwitch FailOperations(
        string path, StorageLegs legs, Func<string, Exception>? error = null, int? times = null)
    {
        var key = Norm(path);
        return FailOperations(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase),
            legs, error, times, key);
    }

    /// <summary>
    /// From now on, a read of <c>shipped.Path</c> (<c>Read</c>, or a <c>ReadMany</c> naming it)
    /// answers <paramref name="shipped"/> whenever the store holds no row there — the shape of a
    /// READ-ONLY provider (content shipped in an image) under a writable override. Writable storage
    /// is untouched: <c>Exists</c>, <c>ExistsInWritableStorage</c>, writes and deletes see only what
    /// the store holds, as a read-only provider's do. Each read the fallback answered is an arrival.
    /// </summary>
    /// <param name="shipped">The read-only row.</param>
    public FaultSwitch ServeReadOnlyFallback(MeshNode shipped)
    {
        var key = Norm(shipped.Path);
        var fallback = new FaultSwitch($"{Name}: serve a read-only fallback at {key}",
            s => _readOnlyFallbacks.TryRemove(new KeyValuePair<string, (MeshNode, FaultSwitch)>(key, (shipped, s))));
        _readOnlyFallbacks[key] = (shipped, fallback);
        return fallback;
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

    /// <summary>
    /// Declares a hole in this process's cross-process feed, as the PostgreSQL listener does when
    /// it re-opens its LISTEN session after a dropped connection (Plugins#3000). Arrives on
    /// <see cref="ChangeFeedGaps"/>.
    /// </summary>
    /// <param name="gap">The declared gap.</param>
    public void DeliverChangeFeedGap(ChangeFeedGap gap) => _gaps.OnNext(gap);

    // ── IStorageAdapter ──────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IObservable<DataChangeNotification> Changes { get; }

    /// <inheritdoc />
    /// <remarks>Each subscriber isolated — one that throws must not keep the next from seeing the
    /// gap, or the harness would model a weaker contract than production's.</remarks>
    public IObservable<ChangeFeedGap> ChangeFeedGaps => Observable.Create<ChangeFeedGap>(observer =>
        Observable.Merge(_inner.ChangeFeedGaps, _gaps.AsObservable()).Subscribe(
            gap =>
            {
                try { observer.OnNext(gap); }
                catch (Exception) { /* isolated: the throwing consumer is the test's to see, not its siblings' */ }
            },
            observer.OnError,
            observer.OnCompleted));

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => Observable.Defer(() => Injected<MeshNode?>(StorageLegs.Read, path, $"Read {path}")
            ?? (HiddenBy(path) is { } hidden
                ? Answered(hidden, $"Read {path}", (MeshNode?)null)
                : WithFallback(path, _inner.Read(path, options))));

    /// <inheritdoc />
    public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            if (InjectedAny<MeshNode>(StorageLegs.Read, paths, "ReadMany") is { } refused)
                return refused;
            var visible = paths.Where(p =>
            {
                if (HiddenBy(p) is not { } hidden) return true;
                hidden.NoteArrival($"ReadMany {p}");
                return false;
            }).ToList();
            return visible.Count == 0
                ? Observable.Empty<MeshNode>()
                : DelayedBatchRead(visible, WithFallbacks(visible, _inner.ReadMany(visible, options)));
        });

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => Observable.Defer(() => Unreachable<MeshNode?>(node.Path, $"Write {node.Path}")
            ?? Injected<MeshNode?>(StorageLegs.Write, node.Path, $"Write {node.Path}")
            ?? HeldWrite(node, () => _inner.Write(node, options)));

    /// <summary>The refusal a closed <see cref="RefuseWritesAsJudged"/> or
    /// <see cref="RefuseWritesAsUnreachable"/> answers for <paramref name="path"/>, or <c>null</c> when
    /// the store takes the write.</summary>
    private IObservable<T>? Unreachable<T>(string path, string what)
    {
        if (_judgedWrites.TryGetValue(Norm(path), out var judged) && judged.IsClosed)
        {
            judged.NoteArrival(what);
            return Observable.Throw<T>(new InvalidOperationException(
                $"{Name}: injected fault — the store refused the write of {path}"));
        }
        if (!_unreachableWrites.TryGetValue(Norm(path), out var fault) || !fault.IsClosed)
            return null;
        fault.NoteArrival(what);
        return Observable.Throw<T>(new UnreachableStoreException(
            $"{Name}: injected fault — failed to connect to the store for {path}"));
    }

    /// <summary>A driver connection failure (SQLSTATE 08006, <c>connection_failure</c>).</summary>
    private sealed class UnreachableStoreException(string message) : System.Data.Common.DbException(message)
    {
        public override string? SqlState => "08006";
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<MeshNode>> WriteMany(IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            // One held node holds the batch: the batch is one flush, and it lands or waits as one.
            // Capture each switch ONCE: a release between two lookups would otherwise hand the
            // gate a switch that is no longer registered.
            if (nodes.Select(n => Unreachable<IReadOnlyList<MeshNode>>(n.Path, $"WriteMany {n.Path}"))
                    .FirstOrDefault(r => r is not null) is { } refused)
                return refused;
            if (InjectedAny<IReadOnlyList<MeshNode>>(StorageLegs.Write, nodes.Select(n => n.Path), "WriteMany") is { } faulted)
                return faulted;
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
        => Observable.Defer(() =>
        {
            if ((Unreachable<bool?>(node.Path, $"WriteIfVersion {node.Path}")
                 ?? Injected<bool?>(StorageLegs.Write, node.Path, $"WriteIfVersion {node.Path}")) is { } refused)
                return refused;
            if (_conditionalWriteFaults.TryGetValue(Norm(node.Path), out var fault) && fault.IsClosed)
            {
                fault.NoteArrival($"WriteIfVersion {node.Path} v{node.Version}");
                return Observable.Throw<bool?>(new InvalidOperationException(
                    $"{Name}: injected fault — the conditional write of {node.Path} could not reach the store"));
            }
            return HeldWrite(node, () => _inner.WriteIfVersion(node, expectedVersion, options));
        });

    /// <inheritdoc />
    public IObservable<string> Delete(string path) => FaultedDelete([path], "Delete", () => HeldDelete([path], () => Observable.Defer(() =>
    {
        if (!_deleteFaults.TryGetValue(Norm(path), out var fault) || !fault.IsClosed)
            return _inner.Delete(path);
        fault.NoteArrival($"Delete {Norm(path)}");
        return Observable.Throw<string>(new InvalidOperationException(
            $"{Name}: injected fault — Delete of {Norm(path)} failed and removed nothing"));
    })));

    /// <inheritdoc />
    public IObservable<bool> DeleteIfExists(string path)
        => FaultedDelete([path], "DeleteIfExists", () => HeldDelete([path], () => _inner.DeleteIfExists(path)));

    /// <inheritdoc />
    /// <remarks>Held, then decided ATOMICALLY by the store underneath once released; each answer the
    /// store actually gave is recorded on <see cref="ConditionalDeleteResults"/>.</remarks>
    public IObservable<bool> DeleteIfVersionAtMost(string path, long maxVersion, JsonSerializerOptions options)
        => FaultedDelete([path], "DeleteIfVersionAtMost", () => HeldDelete([path], () => _inner.DeleteIfVersionAtMost(path, maxVersion, options)
            .Do(removed => _conditionalDeleteResults.GetOrAdd(Norm(path), _ => new ReplaySubject<bool>()).OnNext(removed))));

    /// <inheritdoc />
    /// <remarks>Held like every delete, then decided ATOMICALLY (version and incarnation) by the store
    /// underneath; its answer is recorded on <see cref="ConditionalDeleteResults"/>.</remarks>
    public IObservable<bool> DeleteIfIncarnationAtMost(
        string path, long maxVersion, DateTimeOffset incarnation, JsonSerializerOptions options)
        => FaultedDelete([path], "DeleteIfIncarnationAtMost", () => HeldDelete([path], () => _inner.DeleteIfIncarnationAtMost(path, maxVersion, incarnation, options)
            .Do(removed => _conditionalDeleteResults.GetOrAdd(Norm(path), _ => new ReplaySubject<bool>()).OnNext(removed))));

    /// <summary>
    /// Each answer the store underneath gave to a compare-and-delete of <paramref name="path"/>
    /// (replayed) — the positive proof that a held delete actually RAN, and what it decided.
    /// </summary>
    /// <param name="path">The node path.</param>
    public IObservable<bool> ConditionalDeleteResults(string path)
        => _conditionalDeleteResults.GetOrAdd(Norm(path), _ => new ReplaySubject<bool>()).AsObservable();

    /// <inheritdoc />
    public IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths)
        => FaultedDelete(paths, "DeleteMany", () => HeldDelete(paths, () => Observable.Defer(() =>
        {
            foreach (var path in paths)
            {
                if (!_deleteManyFaults.TryGetValue(Norm(path), out var fault) || !fault.IsClosed)
                    continue;
                var stop = Norm(path);
                fault.NoteArrival($"DeleteMany [{string.Join(", ", paths.Select(Norm))}] stops at {stop}");
                var others = paths.Where(p => !string.Equals(Norm(p), stop, StringComparison.OrdinalIgnoreCase)).ToList();
                // The store died part-way, and it says so the way the DeleteMany contract demands
                // (#7396): the fault names the rows that did leave storage.
                return (others.Count == 0
                        ? Observable.Return<IReadOnlyList<string>>(Array.Empty<string>())
                        : _inner.DeleteMany(others))
                    .SelectMany(removed => Observable.Throw<IReadOnlyList<string>>(
                        BatchDeletePartiallyAppliedException.Including(new InvalidOperationException(
                            $"{Name}: injected fault — DeleteMany stopped at {stop} after deleting {removed.Count} other path(s)"),
                            removed)));
            }
            return _inner.DeleteMany(paths);
        })));

    /// <inheritdoc />
    public IObservable<string?> FindDeleteBlockingProvider(string path) => _inner.FindDeleteBlockingProvider(path);

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => Observable.Defer(() =>
        {
            if (Injected<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>(
                    StorageLegs.Enumerate, parentPath, $"ListChildPaths {Norm(parentPath)}") is { } refused)
                return refused;
            NoteEnumeration(parentPath, nameof(ListChildPaths));
            return HeldEnumeration(parentPath, nameof(ListChildPaths), _inner.ListChildPaths(parentPath)).Select(listing => (
                (IEnumerable<string>)listing.NodePaths.Where(p => Visible(p, $"ListChildPaths {parentPath}")).ToList(),
                (IEnumerable<string>)listing.DirectoryPaths.Where(p => Visible(p, $"ListChildPaths {parentPath}")).ToList()));
        });

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => Observable.Defer(() =>
        {
            if (Injected<IReadOnlyCollection<string>>(
                    StorageLegs.Enumerate, rootPath, $"ListDescendantPaths {Norm(rootPath)}") is { } refused)
                return refused;
            NoteEnumeration(rootPath, nameof(ListDescendantPaths));
            return HeldEnumeration(rootPath, nameof(ListDescendantPaths), _inner.ListDescendantPaths(rootPath))
                .Select(set => (IReadOnlyCollection<string>)set.Where(p => Visible(p, $"ListDescendantPaths {rootPath}")).ToList());
        });

    /// <inheritdoc />
    public IObservable<bool> Exists(string path)
        => Observable.Defer(() => Injected<bool>(StorageLegs.Exists, path, $"Exists {path}")
            ?? (HiddenBy(path) is { } hidden
                ? Answered(hidden, $"Exists {path}", false)
                : _inner.Exists(path)));

    /// <inheritdoc />
    public IObservable<bool> ExistsInWritableStorage(string path)
        => Observable.Defer(() => Injected<bool>(StorageLegs.Exists, path, $"ExistsInWritableStorage {path}")
            ?? (HiddenBy(path) is { } hidden
                ? Answered(hidden, $"ExistsInWritableStorage {path}", false)
                : _inner.ExistsInWritableStorage(path)));

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

    /// <summary>
    /// The NEXT batch read (<c>ReadMany</c>) naming <paramref name="path"/> is DECIDED by the store at
    /// once and DELIVERED when the returned switch is released: the <b>answer in flight</b>. A mesh
    /// resolves a route with one such batch read over the path's prefixes, so this is the shape of a
    /// "no node found" reached before the node was created again and received after it — an ordering no
    /// clock can force. One-shot: every later read passes untouched, so the recreate and the reads after
    /// it run normally while the captured answer waits. The captured read is the switch's arrival.
    /// </summary>
    /// <param name="path">The node path whose next batch read to capture.</param>
    public FaultSwitch DelayNextBatchReadAnswer(string path)
    {
        var key = Norm(path);
        var delay = new FaultSwitch($"{Name}: delay the next batch read answer naming {key}",
            s => _batchReadDelays.TryRemove(new KeyValuePair<string, FaultSwitch>(key, s)));
        if (!_batchReadDelays.TryAdd(key, delay))
            throw new InvalidOperationException($"A batch read of {key} is already being delayed.");
        return delay;
    }

    private readonly ConcurrentDictionary<string, FaultSwitch> _batchReadDelays = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="answer"/> unchanged — unless it is the first batch read naming a path armed with
    /// <see cref="DelayNextBatchReadAnswer"/>, which is claimed here (one-shot), subscribed EAGERLY so the
    /// store decides it now, and replayed behind the switch.
    /// </summary>
    private IObservable<T> DelayedBatchRead<T>(IReadOnlyCollection<string> paths, IObservable<T> answer)
    {
        if (_batchReadDelays.IsEmpty)
            return answer;
        foreach (var path in paths)
        {
            var key = Norm(path);
            if (!_batchReadDelays.TryGetValue(key, out var delay)
                || !delay.IsClosed
                || !_batchReadDelays.TryRemove(new KeyValuePair<string, FaultSwitch>(key, delay)))
                continue;
            var decided = answer.Replay();
            decided.Connect();
            return delay.Gate(decided, $"ReadMany {string.Join(", ", paths)}");
        }
        return answer;
    }

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

    /// <summary>One <see cref="FailOperations(Func{string, bool}, StorageLegs, Func{string, Exception}?, int?, string?)"/> in force.</summary>
    private sealed record OperationFault(
        Func<string, bool> Matches, StorageLegs Legs, Func<string, Exception> Error, FaultSwitch Switch);

    /// <summary>The fault an operation fault in force answers for <paramref name="leg"/> of
    /// <paramref name="path"/>, or <c>null</c> when the store takes the call. Deciding it is an
    /// APPLICATION of the fault (<see cref="FaultSwitch.TryApply"/>), so it is called on subscribe only.</summary>
    private IObservable<T>? Injected<T>(StorageLegs leg, string? path, string what)
    {
        var faults = Volatile.Read(ref _operationFaults);
        if (faults.IsEmpty)
            return null;
        var key = Norm(path);
        foreach (var fault in faults)
            if ((fault.Legs & leg) != 0 && fault.Matches(key) && fault.Switch.TryApply(what))
                return Observable.Throw<T>(fault.Error(key));
        return null;
    }

    /// <summary>The fault a batch naming any selected path answers — the first one, applied once.</summary>
    private IObservable<T>? InjectedAny<T>(StorageLegs leg, IEnumerable<string> paths, string call)
        => paths.Select(p => Injected<T>(leg, p, $"{call} {Norm(p)}")).FirstOrDefault(r => r is not null);

    /// <summary>A delete the operation faults refuse, decided on subscribe and ahead of any delete
    /// hold; otherwise <paramref name="delete"/>.</summary>
    private IObservable<T> FaultedDelete<T>(IReadOnlyCollection<string> paths, string call, Func<IObservable<T>> delete)
        => Observable.Defer(() => InjectedAny<T>(StorageLegs.Delete, paths, call) ?? delete());

    private (MeshNode Row, FaultSwitch Switch)? FallbackFor(string? path)
        => _readOnlyFallbacks.TryGetValue(Norm(path), out var fallback) && fallback.Switch.IsClosed
            ? fallback
            : null;

    private IObservable<MeshNode?> WithFallback(string path, IObservable<MeshNode?> read)
        => FallbackFor(path) is not { } fallback
            ? read
            : read.Select(stored =>
            {
                if (stored is not null)
                    return stored;
                fallback.Switch.NoteArrival($"Read {Norm(path)} (served the read-only fallback)");
                return fallback.Row;
            });

    private IObservable<MeshNode> WithFallbacks(IReadOnlyCollection<string> paths, IObservable<MeshNode> read)
    {
        var fallbacks = paths.Select(FallbackFor).Where(f => f is not null).Select(f => f!.Value).ToImmutableList();
        if (fallbacks.IsEmpty)
            return read;
        return read.ToList().SelectMany(found =>
        {
            var stored = found.Select(n => Norm(n.Path)).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            var served = fallbacks.Where(f => !stored.Contains(Norm(f.Row.Path))).ToImmutableList();
            foreach (var f in served)
                f.Switch.NoteArrival($"ReadMany {Norm(f.Row.Path)} (served the read-only fallback)");
            return found.Concat(served.Select(f => f.Row));
        });
    }

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

    /// <remarks>Every closed hold on any of <paramref name="paths"/> holds the delete: after one is
    /// released the check runs again, so a batch waits until the last of its holds is released.</remarks>
    private IObservable<T> HeldDelete<T>(IReadOnlyCollection<string> paths, Func<IObservable<T>> delete)
        => Observable.Defer(() =>
        {
            foreach (var path in paths)
                if (_deleteHolds.TryGetValue(Norm(path), out var hold) && hold.IsClosed)
                    return hold.Gate(HeldDelete(paths, delete), $"Delete {Norm(path)}");
            return delete();
        });

    private IObservable<T> HeldEnumeration<T>(string? path, string call, IObservable<T> enumeration)
        => _enumerationHolds.TryGetValue(Norm(path), out var hold) && hold.IsClosed
            ? hold.Gate(enumeration, $"{call} {Norm(path)}")
            : enumeration;

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
