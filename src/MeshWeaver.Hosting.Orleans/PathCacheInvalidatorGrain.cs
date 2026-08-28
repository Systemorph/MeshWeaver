using System.Linq;
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
///
/// <para>🚨 Every <see cref="MeshChangeKind"/> has a stream, and this grain must subscribe to
/// ALL of them. <c>OrleansMeshChangeFeed</c> broadcasts each change onto
/// <c>mesh-{kind}</c>; until 2026-08-28 this grain subscribed to <c>mesh-created</c> and
/// <c>mesh-deleted</c> only, so an <see cref="MeshChangeKind.Updated"/> event — a node RETYPE,
/// which is an update of an existing path — reached no other silo. Every other silo's
/// <c>PathResolutionService</c> kept the pre-retype node cached and re-bound a recycled hub to
/// the OLD type for the life of the process, and the <c>NodeTypeRebindWatcher</c> there never
/// fired. Measured on the Systemorph portal: eleven partition roots retyped
/// <c>Space → Crm/Client</c> kept activating with the Space areas after every recycle. The
/// attribute list and the subscribe loop below are both derived from the enum so they cannot
/// drift from the publisher again (pinned by <c>PathCacheInvalidatorGrainSubscriptionTest</c>).</para>
/// </summary>
[ImplicitStreamSubscription("mesh-created")]
[ImplicitStreamSubscription("mesh-updated")]
[ImplicitStreamSubscription("mesh-deleted")]
public class PathCacheInvalidatorGrain : Grain, IAsyncObserver<MeshChangeEvent>
{
    /// <summary>The stream namespace a <see cref="MeshChangeKind"/> is broadcast on —
    /// the ONE formula shared with <c>OrleansMeshChangeFeed.BroadcastAsync</c>.</summary>
    public static string StreamNamespaceOf(MeshChangeKind kind) =>
        $"mesh-{kind.ToString().ToLowerInvariant()}";

    /// <summary>Every stream namespace this grain must be subscribed to — one per kind.</summary>
    public static IReadOnlyList<string> StreamNamespaces { get; } =
        Enum.GetValues<MeshChangeKind>().Select(StreamNamespaceOf).ToList();

    private readonly InProcessMeshChangeFeed _localFeed;
    private readonly ILogger<PathCacheInvalidatorGrain>? _logger;

    /// <summary>
    /// Initializes a new instance of the <c>PathCacheInvalidatorGrain</c> class.
    /// </summary>
    /// <param name="localFeed">The in-process change feed that received cross-silo events are relayed to.</param>
    /// <param name="logger">Optional logger for stream subscription diagnostics.</param>
    public PathCacheInvalidatorGrain(
        InProcessMeshChangeFeed localFeed,
        ILogger<PathCacheInvalidatorGrain>? logger = null)
    {
        _localFeed = localFeed;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(StreamProviders.Memory);

        // Subscribe to every stream namespace this grain is implicitly subscribed to — one per
        // MeshChangeKind, derived from the enum so a new kind cannot be forgotten here.
        foreach (var ns in StreamNamespaces)
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

    /// <inheritdoc />
    public Task OnNextAsync(MeshChangeEvent item, StreamSequenceToken? token = null)
    {
        _logger?.LogDebug("PathCacheInvalidatorGrain: received {Kind} {Path} from stream", item.Kind, item.Path);
        _localFeed.PublishLocal(item);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnCompletedAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnErrorAsync(Exception ex)
    {
        _logger?.LogWarning(ex, "PathCacheInvalidatorGrain: stream error");
        return Task.CompletedTask;
    }
}
