using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Orleans-distributed implementation of <see cref="IMeshChangeFeed"/>.
/// Wraps <see cref="InProcessMeshChangeFeed"/> for local subscribers and
/// broadcasts to other silos via Orleans memory streams.
/// Stream publish runs through the hub's execution pipeline.
/// </summary>
public class OrleansMeshChangeFeed : IMeshChangeFeed
{
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
        // Local subscribers get it immediately (synchronous)
        _local.Publish(change);

        // Broadcast to other silos via Orleans stream
        try
        {
            var client = _hub.ServiceProvider.GetService<IClusterClient>();
            if (client == null) return;

            var streamNs = $"mesh-{change.Kind.ToString().ToLowerInvariant()}";
            var streamProvider = client.GetStreamProvider(StreamProviders.Memory);
            var stream = streamProvider.GetStream<MeshChangeEvent>(
                StreamId.Create(streamNs, Guid.Empty));

            // Execute through hub's async pipeline with proper error handling
            _hub.InvokeAsync(async _ =>
            {
                await stream.OnNextAsync(change);
            }, ex =>
            {
                _logger?.LogError(ex, "Failed to broadcast MeshChangeEvent: {Path} {Kind}",
                    change.Path, change.Kind);
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            // Stream provider not available (e.g., during shutdown) — local publish already happened
            _logger?.LogDebug(ex, "Orleans stream broadcast skipped for {Path} {Kind}",
                change.Path, change.Kind);
        }
    }

    public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
        => _local.Subscribe(handler, filter);
}
