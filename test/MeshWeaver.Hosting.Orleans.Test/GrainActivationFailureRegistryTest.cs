using System;
using System.Threading.Tasks;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the invalidation half of <see cref="GrainActivationFailureRegistry"/>:
/// a change-feed broadcast for a path (the recycle broadcast
/// <c>MeshOperations.RecycleCore</c> publishes, or any post-commit write) must
/// clear the stored activation error for that grain key — otherwise
/// <c>RoutingGrain</c>'s NACK fallback keeps serving the STALE pre-recycle error
/// text (e.g. a compile failure that was already fixed) after the node was
/// recycled (the 2026-07-19 memex-cloud <c>AgenticEngineering/Install</c> wedge).
/// Deterministic unit test over the real feed + registry — no cluster, no mocks.
/// </summary>
public class GrainActivationFailureRegistryTest
{
    private const string RecycledPath = "AgenticEngineering/Install";
    private const string OtherPath = "AgenticEngineering/Other";

    private static MeshChangeEvent RecycleBroadcast(string path)
    {
        var segments = path.Split('/');
        return new MeshChangeEvent(
            Namespace: segments.Length > 1 ? string.Join("/", segments[..^1]) : "",
            Id: segments[^1],
            Path: path,
            Kind: MeshChangeKind.Updated,
            NodeType: MeshNode.NodeTypePath,
            Version: 0,
            Timestamp: DateTimeOffset.UtcNow);
    }

    [Fact(Timeout = 30_000)]
    public async Task ChangeFeedBroadcast_ClearsStoredActivationError_ForExactlyThatPath()
    {
        using var feed = new InProcessMeshChangeFeed();
        using var registry = new GrainActivationFailureRegistry(feed);

        registry.Record(RecycledPath, "Compilation failed for 'Edu/CourseInvite': CS0246 …");
        registry.Record(OtherPath, "Compilation failed for 'Edu/Other': CS1501 …");
        registry.TryGet(RecycledPath).Should().NotBeNull("precondition: the error is stored");

        // 🚨 The feed fans out on its OWN serial dispatch loop, never the publisher's
        // thread (issue #899), so "cleared by the time Publish returns" was never the
        // contract — in Orleans this same invalidation already arrived asynchronously,
        // relayed cross-silo by PathCacheInvalidatorGrain, and its only consumer
        // (RoutingGrain's NACK fallback) reads the registry on a LATER message delivery.
        // This probe subscribes AFTER the registry did, and the one FIFO loop notifies
        // observers in subscription order — so the probe firing PROVES the registry has
        // already handled the same event. Deterministic, no sleep, no polling.
        var handledByRegistry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var probe = feed.Subscribe(e =>
        {
            if (e.Path == RecycledPath)
                handledByRegistry.TrySetResult();
        });

        feed.Publish(RecycleBroadcast(RecycledPath));
        await handledByRegistry.Task.WaitAsync(TimeSpan.FromSeconds(15));

        registry.TryGet(RecycledPath).Should().BeNull(
            "the recycle broadcast must clear the stale activation error so it is never " +
            "NACKed to a sender after the node was recycled");
        registry.TryGet(OtherPath).Should().NotBeNull(
            "the reset is scoped to the broadcast path — other grains' errors stay");
    }

    [Fact]
    public void WithoutChangeFeed_RegistryStillRecordsAndClearsManually()
    {
        using var registry = new GrainActivationFailureRegistry();
        registry.Record(RecycledPath, "boom");
        registry.TryGet(RecycledPath).Should().Be("boom");
        registry.Clear(RecycledPath);
        registry.TryGet(RecycledPath).Should().BeNull();
    }
}
