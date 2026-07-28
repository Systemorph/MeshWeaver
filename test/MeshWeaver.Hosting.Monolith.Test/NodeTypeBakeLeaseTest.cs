using System;
using System.IO;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The single-baker lease. Without it, every replica on a new image finds the same framework-stale
/// cache and starts the same sweep into the same volume — concurrent cold compiles of one NodeType,
/// which is exactly the storm the sequential sweep exists to prevent.
///
/// <para>Three properties matter, and each has a failure mode worse than the problem it solves:
/// mutual exclusion (or the storm returns), stealability (or one dead pod wedges the fleet forever),
/// and fail-open (or a volume blip stops the fleet compiling at all).</para>
/// </summary>
public class NodeTypeBakeLeaseTest : IDisposable
{
    private readonly string dir = Path.Combine(
        Path.GetTempPath(), $"mw-bake-lease-{Guid.NewGuid():N}");

    private const string Framework = "03d6f01eb6654e199d31fc59668d7b62";

    public NodeTypeBakeLeaseTest() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Exactly one holder — the property the whole class exists for.</summary>
    [Fact]
    public void SecondPod_DoesNotGetTheLease()
    {
        using var first = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-a");
        var second = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-b");

        first.Should().NotBeNull("the first pod to arrive bakes");
        second.Should().BeNull("a second pod must FOLLOW, never bake concurrently");
    }

    /// <summary>Releasing hands the bake straight to the next pod — no waiting out the stale window.</summary>
    [Fact]
    public void ReleasingTheLease_LetsTheNextPodTakeIt()
    {
        var first = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-a");
        first.Should().NotBeNull();
        first!.Dispose();

        using var second = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-b");
        second.Should().NotBeNull("a released lease is immediately available");
    }

    /// <summary>
    /// 🚨 The fleet-wedge guard. A pod that dies mid-bake cannot hold the bake hostage: once its
    /// heartbeat goes stale another pod takes over. Without this, one crash means nothing ever
    /// compiles again and every subsequent rollout stalls on a gate that can never go green.
    /// </summary>
    [Fact]
    public void StaleLease_IsStolen()
    {
        using var dead = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-that-dies");
        dead.Should().NotBeNull();

        // Age the heartbeat past the staleness limit, as a crashed holder's would.
        var path = NodeTypeBakeLease.PathFor(dir, Framework);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - NodeTypeBakeLease.StaleAfter - TimeSpan.FromMinutes(1));

        using var successor = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-b");
        successor.Should().NotBeNull("a lease whose holder stopped heartbeating must be stealable");
    }

    /// <summary>
    /// A live holder's lease is NOT stolen just because the bake is long. Stealing early would put
    /// two pods on the same compile — the failure this prevents, reintroduced.
    /// </summary>
    [Fact]
    public void FreshLease_IsNotStolen()
    {
        using var holder = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-a");
        holder.Should().NotBeNull();

        var path = NodeTypeBakeLease.PathFor(dir, Framework);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - NodeTypeBakeLease.StaleAfter + TimeSpan.FromMinutes(2));

        NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-b")
            .Should().BeNull("a holder that is still heartbeating keeps the bake");
    }

    /// <summary>
    /// Keyed per framework: a bake-ahead pod on a NEW image and the live pods on the OLD one write
    /// different files and must not block each other. Only same-image replicas contend.
    /// </summary>
    [Fact]
    public void DifferentFrameworkVersions_DoNotContend()
    {
        using var oldImage = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-old");
        using var newImage = NodeTypeBakeLease.TryAcquire(dir, "b7e11c9a44d24f0d8e2a5c31f9048ab6", "pod-new");

        oldImage.Should().NotBeNull();
        newImage.Should().NotBeNull("a different image bakes different files — it must not be blocked");
    }

    /// <summary>
    /// Fail OPEN. An unusable coordination directory must still let the pod bake: duplicate work is a
    /// cost, a fleet that never compiles is an outage.
    /// </summary>
    [Fact]
    public void UnusableDirectory_StillAllowsBaking()
    {
        // A FILE where the lease directory should be — CreateDirectory and the lease write both fail.
        var blocked = Path.Combine(dir, "not-a-directory");
        File.WriteAllText(blocked, "x");

        using var lease = NodeTypeBakeLease.TryAcquire(blocked, Framework, "pod-a");
        lease.Should().NotBeNull("coordination failure must never deny the bake");
    }

    [Fact]
    public void HeartbeatInterval_IsWellInsideTheStaleWindow() =>
        NodeTypeBakeLease.HeartbeatInterval.Should()
            .BeLessThan(NodeTypeBakeLease.StaleAfter / 2,
                "a single missed beat under load must not hand the bake to a second pod while the "
                + "first is still compiling");

    [Fact]
    public void DisposingTwice_IsHarmless()
    {
        var lease = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-a");
        lease.Should().NotBeNull();
        lease!.Dispose();
        lease.Dispose();
    }
}
