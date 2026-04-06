using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Grain that receives MeshChangeEvent broadcasts from other silos via Orleans streams
/// and relays them to the local <see cref="InProcessMeshChangeFeed"/>.
///
/// One instance per silo per stream namespace is activated implicitly.
/// Uses <see cref="InProcessMeshChangeFeed.PublishLocal"/> to avoid re-broadcasting.
/// </summary>
[ImplicitStreamSubscription("mesh-created")]
[ImplicitStreamSubscription("mesh-deleted")]
public class PathCacheInvalidatorGrain : Grain, IAsyncObserver<MeshChangeEvent>
{
    private readonly InProcessMeshChangeFeed _localFeed;
    private readonly ILogger<PathCacheInvalidatorGrain>? _logger;

    public PathCacheInvalidatorGrain(
        InProcessMeshChangeFeed localFeed,
        ILogger<PathCacheInvalidatorGrain>? logger = null)
    {
        _localFeed = localFeed;
        _logger = logger;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(StreamProviders.Memory);

        // Subscribe to all stream namespaces this grain is implicitly subscribed to
        foreach (var ns in new[] { "mesh-created", "mesh-deleted" })
        {
            var stream = streamProvider.GetStream<MeshChangeEvent>(
                StreamId.Create(ns, this.GetPrimaryKey()));

            // Check for existing subscriptions (resume after reactivation)
            var handles = await stream.GetAllSubscriptionHandles();
            if (handles is { Count: > 0 })
            {
                foreach (var handle in handles)
                    await handle.ResumeAsync(this);
            }
            else
            {
                await stream.SubscribeAsync(this);
            }
        }
    }

    public Task OnNextAsync(MeshChangeEvent item, StreamSequenceToken? token = null)
    {
        _logger?.LogDebug("PathCacheInvalidatorGrain: received {Kind} {Path} from stream", item.Kind, item.Path);
        _localFeed.PublishLocal(item);
        return Task.CompletedTask;
    }

    public Task OnCompletedAsync() => Task.CompletedTask;

    public Task OnErrorAsync(Exception ex)
    {
        _logger?.LogWarning(ex, "PathCacheInvalidatorGrain: stream error");
        return Task.CompletedTask;
    }
}
