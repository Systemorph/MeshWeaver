using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Orleans-distributed implementation of <see cref="IMeshChangeFeed"/>.
/// Wraps <see cref="InProcessMeshChangeFeed"/> for local subscribers and ships each
/// event onto the Orleans memory stream through a serial broadcast queue: every
/// event is ONE pooled async leaf (<c>stream.OnNextAsync</c> via <see cref="IIoPool"/>),
/// composed with <c>Concat</c> so events land on the stream in arrival order — the
/// ordering the previous await-in-handler provided, without an async hub handler
/// (AsynchronousCalls.md: handlers never await).
/// </summary>
public class OrleansMeshChangeFeed : IMeshChangeFeed, IDisposable
{
    private readonly InProcessMeshChangeFeed _local;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OrleansMeshChangeFeed>? _logger;
    private readonly Subject<MeshChangeEvent> _broadcastQueue = new();
    private readonly IDisposable _broadcastSubscription;

    /// <summary>
    /// Initializes a new instance of the <c>OrleansMeshChangeFeed</c> class, wrapping the
    /// in-process feed and wiring the serial broadcast queue that ships events onto the
    /// Orleans memory stream in arrival order.
    /// </summary>
    /// <param name="localFeed">The in-process change feed serving local subscribers and providing local publish/subscribe.</param>
    /// <param name="serviceProvider">
    /// The mesh service provider used to LAZILY resolve the I/O pool (at construction) and the
    /// Orleans cluster client (at broadcast time). 🚨 Captured as <see cref="IServiceProvider"/>,
    /// NOT <see cref="IMessageHub"/>: this feed is constructed from
    /// <c>Workspace..ctor → TrySubscribeToChangeFeed(hub.ServiceProvider)</c> mid-hub-build, so a
    /// factory that resolved <c>IMessageHub</c> here re-entered <c>BuildHub → new Workspace →
    /// IMeshChangeFeed → …</c> and stack-overflowed. Holding the provider and resolving the pieces
    /// on demand breaks that cycle (the cluster client is only touched once the host is fully up).
    /// </param>
    /// <param name="logger">Optional logger for broadcast diagnostics and failures.</param>
    public OrleansMeshChangeFeed(
        InProcessMeshChangeFeed localFeed,
        IServiceProvider serviceProvider,
        ILogger<OrleansMeshChangeFeed>? logger = null)
    {
        _local = localFeed;
        _serviceProvider = serviceProvider;
        _logger = logger;

        var ioPool = serviceProvider.GetService<IoPoolRegistry>()?.Get("orleans-broadcast")
                     ?? IoPool.Unbounded;
        // Per-event Catch: one failed broadcast is logged and skipped — it must not
        // tear down the queue (matches the old per-event try/catch). A fault of the
        // queue plumbing itself is terminal and loud.
        _broadcastSubscription = _broadcastQueue
            .Select(change => ioPool
                .Invoke(ct => BroadcastAsync(change, ct))
                .Catch((Exception ex) =>
                {
                    _logger?.LogError(ex,
                        "OrleansMeshChangeFeed: stream broadcast failed for {Path} {Kind}",
                        change.Path, change.Kind);
                    return Observable.Empty<Unit>();
                }))
            .Concat()
            .Subscribe(
                _ => { },
                ex => _logger?.LogError(ex,
                    "OrleansMeshChangeFeed: broadcast queue faulted — cross-silo broadcasts stopped"));
    }

    /// <inheritdoc />
    public void Publish(MeshChangeEvent change)
    {
        // Local subscribers get it immediately (synchronous).
        _local.Publish(change);

        // Cross-silo broadcast: enqueue onto the serial queue and return —
        // no await, no thread-pool task, no hub handler involved.
        _broadcastQueue.OnNext(change);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
        => _local.Subscribe(handler, filter);

    private async Task<Unit> BroadcastAsync(MeshChangeEvent change, CancellationToken ct)
    {
        var client = _serviceProvider.GetService<IClusterClient>();
        if (client == null)
        {
            _logger?.LogDebug("OrleansMeshChangeFeed: no IClusterClient — broadcast skipped for {Path} {Kind}",
                change.Path, change.Kind);
            return Unit.Default;
        }

        var streamNs = $"mesh-{change.Kind.ToString().ToLowerInvariant()}";
        var streamProvider = client.GetStreamProvider(StreamProviders.Memory);
        var stream = streamProvider.GetStream<MeshChangeEvent>(
            StreamId.Create(streamNs, Guid.Empty));

        await stream.OnNextAsync(change);
        return Unit.Default;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _broadcastQueue.OnCompleted();
        _broadcastSubscription.Dispose();
        _broadcastQueue.Dispose();
    }
}
