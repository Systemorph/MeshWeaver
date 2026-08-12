using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Persistence.PartitionStorage;

/// <summary>
/// Per-hub <see cref="IStorageAdapter"/> proxy that forwards every storage
/// call directly to the partition-hub that owns the target table.
///
/// <para>This proxy is registered <b>per hub</b> via
/// <c>ConfigureHub WithServices</c>, so each caller hub gets its own
/// instance bound to <i>that</i> hub. When a handler on hub <c>A</c> calls
/// <c>adapter.Write(node)</c>, the proxy resolves the partition address via
/// the singleton <see cref="PartitionStorageRouter"/> and posts via
/// <c>A.Observe(req, target = partitionAddress)</c> — caller-hub talks
/// straight to partition-hub, no intermediate routing hub on the message
/// path.</para>
///
/// <para>See <c>Doc/Architecture/PartitionStorageHubs.md</c>.</para>
/// </summary>
public sealed class RoutingProxyAdapter : IStorageAdapter
{
    private readonly IMessageHub _callerHub;
    private readonly PartitionStorageRouter _router;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    /// <summary>
    /// Constructs a proxy that posts via <paramref name="callerHub"/> to the
    /// partition hubs registered in <paramref name="router"/>.
    /// </summary>
    public RoutingProxyAdapter(
        IMessageHub callerHub,
        PartitionStorageRouter router,
        Microsoft.Extensions.Logging.ILogger<RoutingProxyAdapter>? logger = null)
    {
        _callerHub = callerHub;
        _router = router;
        _logger = logger;

        // Probed once, replayed. A hub with no providers registered answers indeterminate, which
        // keeps the healing behaviour rather than silently switching it off.
        _legacyPartitionExists = Observable
            .Defer(() =>
            {
                var providers = callerHub.ServiceProvider
                    .GetService(typeof(IEnumerable<IPartitionStorageProvider>))
                        as IEnumerable<IPartitionStorageProvider>;
                var all = providers?.ToList() ?? [];
                return all.Count == 0
                    ? Observable.Return<bool?>(null)
                    : Observable
                        .Merge(all.Select(p =>
                            p.PartitionExists(LegacyUserPartitionRepair.LegacyPartition)
                                .Catch((Exception _) => Observable.Return<bool?>(null))))
                        .ToList()
                        .Select(answers => answers.Any(a => a is true)
                            ? true
                            : answers.All(a => a is false) ? false : (bool?)null);
            })
            .Replay(1)
            .RefCount();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Wrapped in the legacy-user repair (see <see cref="LegacyUserPartitionRepair"/>): the proxy
    /// is the one seam that can read the legacy <c>User/{id}</c> partition AND write the repaired
    /// root into the <c>{id}</c> partition — each routed to its own partition hub.
    /// </remarks>
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => LegacyUserPartitionRepair.ReadWithRepair(
            path,
            p => ReadCore(p, options),
            n => Write(n, options),
            _logger,
            LegacyPartitionExists);

    /// <summary>
    /// Does the legacy <c>User</c> partition exist? Same OR-fold and same reason as
    /// <c>PersistenceService.LegacyPartitionExists</c> — and this seam needs it MORE, not less:
    /// it is the one that actually routes a <c>User/{id}</c> read to a partition hub, so on a
    /// store that never had a legacy partition it is where the probe turns a root miss into a
    /// <c>42P01</c>. Resolved from the caller hub (the proxy is registered per hub) and answered
    /// once, replayed.
    /// </summary>
    private IObservable<bool?> LegacyPartitionExists() => _legacyPartitionExists;

    private readonly IObservable<bool?> _legacyPartitionExists;

    private IObservable<MeshNode?> ReadCore(string path, JsonSerializerOptions options)
        => _router.AddressFor(path).SelectMany(addr =>
            addr is null
                ? Observable.Return<MeshNode?>(null)
                : _callerHub
                    .Observe<ReadNodeResponse>(new ReadNodeRequest(path, options), o => o.WithTarget(addr))
                    .Take(1)
                    .Select(d => d.Message.Node));

    /// <inheritdoc/>
    /// <remarks>
    /// Returns null when no partition-storage hub claims the path so the
    /// outer try-then-claim chain (<see cref="PersistenceService.Write"/>)
    /// can fall through to the next writable provider.
    /// </remarks>
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => _router.AddressFor(node.Path).SelectMany(addr =>
            addr is null
                ? Observable.Return<MeshNode?>(null)
                : _callerHub
                    .Observe<WriteBatchResponse>(
                        new WriteBatchRequest(ImmutableList.Create(node), options),
                        o => o.WithTarget(addr))
                    .Take(1)
                    .SelectMany(d => d.Message.Error != null
                        ? Observable.Throw<MeshNode?>(new InvalidOperationException(d.Message.Error))
                        : Observable.Return<MeshNode?>(d.Message.WrittenNodes.First())));

    /// <inheritdoc/>
    /// <remarks>
    /// The multi-node producer <see cref="WriteBatchRequest"/> was designed for: groups the
    /// caller's nodes by owning partition-storage hub and posts ONE batch per group, so a
    /// course-sized install is a handful of messages (and, on Postgres, a handful of
    /// <c>NpgsqlBatch</c> round-trips) instead of one message per node. Before this override the
    /// interface default degraded every batch back into per-node <see cref="Write"/> calls — the
    /// single-node producer the WriteMany commit set out to retire.
    ///
    /// <para>Order: the caller's order is preserved by grouping CONSECUTIVE RUNS of the same
    /// target address (never re-sorting across runs) and posting the runs sequentially —
    /// <see cref="IStorageAdapter.WriteMany"/>'s contract pins the caller's order onto the
    /// <see cref="IStorageAdapter.Changes"/> feed, and callers order parents before children on
    /// purpose. Nodes no partition hub claims are absent from the result, mirroring
    /// <see cref="Write"/>'s null. A batch error fails the whole observable, mirroring
    /// <see cref="Write"/>'s throw.</para>
    /// </remarks>
    public IObservable<IReadOnlyList<MeshNode>> WriteMany(
        IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
    {
        if (nodes.Count == 0)
            return Observable.Return<IReadOnlyList<MeshNode>>(ImmutableList<MeshNode>.Empty);

        // Resolve every node's owning hub IN CALLER ORDER (Concat, not Merge — order is contract).
        return nodes
            .Select(n => _router.AddressFor(n.Path).Take(1).Select(addr => (Node: n, Address: addr)))
            .ToObservable().Concat().ToList()
            .SelectMany(pairs =>
            {
                // Consecutive runs of the same address — one WriteBatchRequest per run.
                var runs = new List<(Address? Address, List<MeshNode> Nodes)>();
                foreach (var (node, address) in pairs)
                {
                    if (runs.Count == 0 || !Equals(runs[^1].Address, address))
                        runs.Add((address, new List<MeshNode>()));
                    runs[^1].Nodes.Add(node);
                }

                return runs
                    .Select(run => run.Address is null
                        // Unowned: absent from the result so the try-then-claim chain moves on.
                        ? Observable.Return<IReadOnlyList<MeshNode>>(ImmutableList<MeshNode>.Empty)
                        : _callerHub
                            .Observe<WriteBatchResponse>(
                                new WriteBatchRequest(run.Nodes.ToImmutableList(), options),
                                o => o.WithTarget(run.Address))
                            .Take(1)
                            .SelectMany(d => d.Message.Error != null
                                ? Observable.Throw<IReadOnlyList<MeshNode>>(
                                    new InvalidOperationException(d.Message.Error))
                                : Observable.Return<IReadOnlyList<MeshNode>>(d.Message.WrittenNodes)))
                    .ToObservable().Concat().ToList()
                    .Select(lists => (IReadOnlyList<MeshNode>)lists
                        .SelectMany(l => l).ToImmutableList());
            });
    }

    /// <inheritdoc/>
    public IObservable<string> Delete(string path)
        => _router.AddressFor(path).SelectMany(addr =>
            addr is null
                ? Observable.Return(path)
                : _callerHub
                    .Observe<DeleteBatchResponse>(
                        new DeleteBatchRequest(ImmutableList.Create(path)),
                        o => o.WithTarget(addr))
                    .Take(1)
                    .Select(d => d.Message.Error != null ? path : d.Message.DeletedPaths.FirstOrDefault() ?? path));

    /// <inheritdoc/>
    public IObservable<bool> Exists(string path)
        => _router.AddressFor(path).SelectMany(addr =>
            addr is null
                ? Observable.Return(false)
                : _callerHub
                    .Observe<ExistsResponse>(new ExistsRequest(path), o => o.WithTarget(addr))
                    .Take(1)
                    .Select(d => d.Message.Exists));

    /// <inheritdoc/>
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
    {
        // Root-level (null/empty) listing must aggregate every partition's
        // root — the proxy alone can't fan out without knowing all schemas.
        // TODO: route this through a dedicated PartitionStorageRouter API
        // that returns the union; today returns empty so callers fall back
        // to query-driven discovery.
        if (string.IsNullOrEmpty(parentPath))
            return Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []));

        return _router.AddressFor(parentPath).SelectMany(addr =>
            addr is null
                ? Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []))
                : _callerHub
                    .Observe<ListChildPathsResponse>(new ListChildPathsRequest(parentPath), o => o.WithTarget(addr))
                    .Take(1)
                    .Select(d => ((IEnumerable<string>)d.Message.NodePaths, (IEnumerable<string>)d.Message.DirectoryPaths)));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Routed to the partition hub owning the root's partition (every strict
    /// descendant shares the root's first segment) so the answer comes from the
    /// backing adapter's NATIVE prefix enumeration — the interface default would
    /// degrade into a level-by-level <see cref="ListChildPathsRequest"/> walk that
    /// under-enumerates flat single-level backends. A reported error is re-thrown:
    /// the caller is the recursive-delete verifier and must fail loudly rather
    /// than treat an unenumerated subtree as drained.
    /// </remarks>
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => _router.AddressFor(rootPath).SelectMany(addr =>
            addr is null
                ? Observable.Return<IReadOnlyCollection<string>>(ImmutableList<string>.Empty)
                : _callerHub
                    .Observe<ListDescendantPathsResponse>(
                        new ListDescendantPathsRequest(rootPath), o => o.WithTarget(addr))
                    .Take(1)
                    .SelectMany(d => d.Message.Error != null
                        ? Observable.Throw<IReadOnlyCollection<string>>(
                            new InvalidOperationException(d.Message.Error))
                        : Observable.Return<IReadOnlyCollection<string>>(d.Message.Paths)));

    // ── Partition objects: not yet routed via hub messages. ─────────────
    //
    // The new partition-storage hub config does not yet carry partition-
    // object message types. Callers of these methods are limited to a few
    // hosts (Aspire / file-system mirrors / Postgres-specific JSON config
    // export) — leaving stubs that no-op until the partition-object
    // surface is migrated in a follow-up.

    /// <inheritdoc/>
    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => Observable.Empty<object>();

    /// <inheritdoc/>
    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => Observable.Return(Unit.Default);

    /// <inheritdoc/>
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => Observable.Return(Unit.Default);

    /// <inheritdoc/>
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => Observable.Return<DateTimeOffset?>(null);
}
