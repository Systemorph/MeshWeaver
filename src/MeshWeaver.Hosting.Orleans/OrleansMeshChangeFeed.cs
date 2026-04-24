using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Orleans-distributed implementation of <see cref="IMeshChangeFeed"/>.
/// Wraps <see cref="InProcessMeshChangeFeed"/> for local subscribers and dispatches
/// cross-silo broadcast through a dedicated hosted hub. The hosted hub serialises
/// broadcast work on its own ActionBlock — fire-and-forget from the caller, but
/// ordered + observable from the framework's perspective. No raw thread-pool tasks,
/// no <c>InvokeAsync</c> on the calling hub (which would intermix with handler work).
/// </summary>
public class OrleansMeshChangeFeed : IMeshChangeFeed
{
    /// <summary>Reserved address type for the broadcast dispatcher hub.</summary>
    internal const string BroadcastHubType = "mesh-change-broadcast";
    private static readonly Address BroadcastHubAddress = new(BroadcastHubType, "default");

    private readonly InProcessMeshChangeFeed _local;
    private readonly IMessageHub _hub;
    private readonly ILogger<OrleansMeshChangeFeed>? _logger;

    public OrleansMeshChangeFeed(
        InProcessMeshChangeFeed localFeed,
        IMessageHub hub,
        ILogger<OrleansMeshChangeFeed>? logger = null)
    {
        _local = localFeed;
        _hub = hub;
        _logger = logger;
    }

    public void Publish(MeshChangeEvent change)
    {
        // Local subscribers get it immediately (synchronous).
        _local.Publish(change);

        // Cross-silo broadcast: dispatch to the broadcast hosted hub. The hub's
        // handler does the actual stream.OnNextAsync via Observable.FromAsync.
        // From here we just post and return — no await, no thread-pool task.
        var broadcaster = _hub.GetHostedHub(BroadcastHubAddress, BroadcastHubConfiguration);
        broadcaster?.Post(new BroadcastChangeEventRequest(change));
    }

    public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
        => _local.Subscribe(handler, filter);

    /// <summary>
    /// Configures the broadcast hosted hub. Single async handler that ships each event
    /// onto the Orleans memory stream. The handler is allowed to <c>await</c> here —
    /// it runs on the dispatcher hub's own ActionBlock, separate from the parent hub
    /// the caller posts from, so awaiting does not block any caller.
    /// </summary>
    private static MessageHubConfiguration BroadcastHubConfiguration(MessageHubConfiguration config)
        => config.WithHandler<BroadcastChangeEventRequest>(HandleBroadcast);

    private static async Task<IMessageDelivery> HandleBroadcast(
        IMessageHub hub,
        IMessageDelivery<BroadcastChangeEventRequest> request,
        CancellationToken ct)
    {
        var change = request.Message.Change;
        var logger = hub.ServiceProvider.GetService<ILogger<OrleansMeshChangeFeed>>();
        var client = hub.ServiceProvider.GetService<IClusterClient>();
        if (client == null)
        {
            logger?.LogDebug("OrleansMeshChangeFeed: no IClusterClient — broadcast skipped for {Path} {Kind}",
                change.Path, change.Kind);
            return request.Processed();
        }

        var streamNs = $"mesh-{change.Kind.ToString().ToLowerInvariant()}";
        var streamProvider = client.GetStreamProvider(StreamProviders.Memory);
        var stream = streamProvider.GetStream<MeshChangeEvent>(
            StreamId.Create(streamNs, Guid.Empty));

        // Awaiting is fine inside this dispatcher hub's handler — it runs on the
        // broadcast hub's own ActionBlock, not the caller's. The caller already
        // returned (Publish is fire-and-forget). This await sequences events onto
        // the Orleans stream in arrival order.
        try
        {
            await stream.OnNextAsync(change);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "OrleansMeshChangeFeed: stream broadcast failed for {Path} {Kind}",
                change.Path, change.Kind);
        }
        return request.Processed();
    }
}

/// <summary>
/// Internal envelope for dispatching a <see cref="MeshChangeEvent"/> to the broadcast
/// hosted hub. Not intended for use outside <see cref="OrleansMeshChangeFeed"/>.
/// </summary>
internal record BroadcastChangeEventRequest(MeshChangeEvent Change);
