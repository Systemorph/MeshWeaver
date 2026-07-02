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
            _logger);

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
