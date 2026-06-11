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
    private readonly IMessageHub _hub;
    private readonly ILogger<OrleansMeshChangeFeed>? _logger;
    private readonly Subject<MeshChangeEvent> _broadcastQueue = new();
    private readonly IDisposable _broadcastSubscription;

    public OrleansMeshChangeFeed(
        InProcessMeshChangeFeed localFeed,
        IMessageHub hub,
        ILogger<OrleansMeshChangeFeed>? logger = null)
    {
        _local = localFeed;
        _hub = hub;
        _logger = logger;

        var ioPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get("orleans-broadcast")
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

    public void Publish(MeshChangeEvent change)
    {
        // Local subscribers get it immediately (synchronous).
        _local.Publish(change);

        // Cross-silo broadcast: enqueue onto the serial queue and return —
        // no await, no thread-pool task, no hub handler involved.
        _broadcastQueue.OnNext(change);
    }

    public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
        => _local.Subscribe(handler, filter);

    private async Task<Unit> BroadcastAsync(MeshChangeEvent change, CancellationToken ct)
    {
        var client = _hub.ServiceProvider.GetService<IClusterClient>();
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

    public void Dispose()
    {
        _broadcastQueue.OnCompleted();
        _broadcastSubscription.Dispose();
        _broadcastQueue.Dispose();
    }
}
