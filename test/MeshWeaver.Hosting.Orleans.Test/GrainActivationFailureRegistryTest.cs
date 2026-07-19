using System;
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

    [Fact]
    public void ChangeFeedBroadcast_ClearsStoredActivationError_ForExactlyThatPath()
    {
        using var feed = new InProcessMeshChangeFeed();
        using var registry = new GrainActivationFailureRegistry(feed);

        registry.Record(RecycledPath, "Compilation failed for 'Edu/CourseInvite': CS0246 …");
        registry.Record(OtherPath, "Compilation failed for 'Edu/Other': CS1501 …");
        registry.TryGet(RecycledPath).Should().NotBeNull("precondition: the error is stored");

        feed.Publish(RecycleBroadcast(RecycledPath));

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
