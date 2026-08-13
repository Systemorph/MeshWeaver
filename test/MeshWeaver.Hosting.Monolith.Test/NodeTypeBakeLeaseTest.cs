using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The single-baker lease. Without it, every replica on a new image finds the same framework-stale
/// cache and starts the same sweep into the same volume — concurrent cold compiles of one NodeType,
/// which is exactly the storm the sequential sweep exists to prevent.
///
/// <para>Four properties matter, and each has a failure mode worse than the problem it solves:
/// mutual exclusion (or the storm returns), takeover from a dead holder (or one dead pod wedges the
/// fleet forever), NON-takeover from a live one (or the storm returns through the back door), and a
/// fail-open path for a broken substrate (or a volume blip stops the fleet compiling at all).</para>
///
/// <para>🚨 The middle two used to be decided by a 10-minute clock over an SMB file timestamp — the
/// weakness in #1355. They are now decided by cluster membership, and the clock survives only as the
/// fallback for a host with no cluster. The tests below pin both regimes, because the fallback is
/// what every monolith and test host actually runs.</para>
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

    // ---- membership decides takeover ---------------------------------------------------------

    /// <summary>
    /// 🚨 The #1355 fix. A pod that dies mid-bake is taken over as soon as MEMBERSHIP says it left —
    /// with a heartbeat only seconds old, and with no staleness budget to wait out. Before this, the
    /// fleet sat on a 10-minute clock for information the cluster already had.
    /// </summary>
    [Fact]
    public void DeadHolder_IsTakenOverImmediately_WithoutWaitingOutTheClock()
    {
        using var dead = NodeTypeBakeLease.TryAcquire(
            dir, Framework, "pod-that-dies", null, new FakeCluster("silo-a"));
        dead.Should().NotBeNull();

        // The heartbeat is FRESH — under the old rule this lease was untouchable for ten minutes.
        // The peer's membership has recorded silo-a as departed.
        using var successor = NodeTypeBakeLease.TryAcquire(
            dir, Framework, "pod-b", null, new FakeCluster("silo-b", gone: ["silo-a"]));

        successor.Should().NotBeNull(
            "membership already knows the holder left — the fleet must not wait out a clock for it");
    }

    /// <summary>
    /// 🚨 The other half, and the cure for the SMB-timestamp weakness: a holder membership reports
    /// LIVE keeps the bake even when its heartbeat reads as ancient. A cached-stale metadata read can
    /// no longer put two pods on one compile, because the timestamp is no longer the evidence.
    /// </summary>
    [Fact]
    public void LiveHolder_IsNeverTakenOver_EvenWhenTheHeartbeatLooksAncient()
    {
        using var holder = NodeTypeBakeLease.TryAcquire(
            dir, Framework, "pod-a", null, new FakeCluster("silo-a"));
        holder.Should().NotBeNull();

        BackdateHeartbeat(TimeSpan.FromDays(1));

        NodeTypeBakeLease.TryAcquire(
                dir, Framework, "pod-b", null, new FakeCluster("silo-b", live: ["silo-a"]))
            .Should().BeNull(
                "membership says the holder is running, so the heartbeat is irrelevant — a stale "
                + "timestamp must never be able to hand the bake to a second pod");
    }

    /// <summary>
    /// "I do not know" is not "it is gone". A membership that cannot resolve the holder falls back to
    /// the clock rather than authorising a takeover — otherwise a not-yet-hydrated snapshot on a
    /// freshly-started silo would evict a perfectly live baker.
    /// </summary>
    [Fact]
    public void UnknownHolder_FallsBackToTheClock_RatherThanTakingOver()
    {
        using var holder = NodeTypeBakeLease.TryAcquire(
            dir, Framework, "pod-a", null, new FakeCluster("silo-a"));
        holder.Should().NotBeNull();

        // The peer's membership has no record of silo-a at all — the shape a silo mid-join sees.
        NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-b", null, new FakeCluster("silo-b"))
            .Should().BeNull("an unresolvable holder with a fresh heartbeat is still held");
    }

    /// <summary>A membership service that throws has told us nothing, and nothing is not "gone".</summary>
    [Fact]
    public void AThrowingMembership_IsTreatedAsUnknown_NotAsGone()
    {
        using var holder = NodeTypeBakeLease.TryAcquire(
            dir, Framework, "pod-a", null, new FakeCluster("silo-a"));
        holder.Should().NotBeNull();

        NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-b", null, new ThrowingCluster())
            .Should().BeNull("a membership that errors must never license a takeover");
    }

    // ---- the clock, still the fallback where there is no cluster -------------------------------

    /// <summary>
    /// The fleet-wedge guard, on the no-cluster path every monolith and test host runs: a pod that
    /// dies mid-bake cannot hold the bake hostage once its heartbeat goes stale.
    /// </summary>
    [Fact]
    public void WithNoCluster_AStaleLease_IsStillTakenOver()
    {
        using var dead = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-that-dies");
        dead.Should().NotBeNull();

        BackdateHeartbeat(NodeTypeBakeLease.StaleAfter + TimeSpan.FromMinutes(1));

        using var successor = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-b");
        successor.Should().NotBeNull("with nothing to ask, a lease that stopped beating is takeable");
    }

    /// <summary>
    /// …and a live holder's long bake is NOT taken over on that path either. Taking it early would put
    /// two pods on the same compile — the failure this prevents, reintroduced.
    /// </summary>
    [Fact]
    public void WithNoCluster_AFreshLease_IsNotTakenOver()
    {
        using var holder = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-a");
        holder.Should().NotBeNull();

        BackdateHeartbeat(NodeTypeBakeLease.StaleAfter - TimeSpan.FromMinutes(2));

        NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-b")
            .Should().BeNull("a holder that is still heartbeating keeps the bake");
    }

    /// <summary>
    /// The heartbeat instant is read from the lease's CONTENT, not its last-write metadata. Azure
    /// Files can serve cached metadata, and a falsely-stale metadata read is exactly the misreading
    /// that puts two pods on one compile — so a lease whose CONTENT is fresh survives a back-dated
    /// file timestamp.
    /// </summary>
    [Fact]
    public void TheHeartbeatInstantComesFromTheContent_NotTheFileTimestamp()
    {
        using var holder = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-a");
        holder.Should().NotBeNull();

        // Metadata says long stale; the content still says now.
        File.SetLastWriteTimeUtc(
            NodeTypeBakeLease.PathFor(dir, Framework),
            DateTime.UtcNow - NodeTypeBakeLease.StaleAfter - TimeSpan.FromHours(1));

        NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-b")
            .Should().BeNull("a cached-stale timestamp must not condemn a live holder");
    }

    /// <summary>
    /// A lease written by a PREVIOUS build carries no identity and only two fields. It must still
    /// parse, and still be governed by the clock — a rollout must not turn every in-flight lease into
    /// an unreadable one.
    /// </summary>
    [Fact]
    public void APreMembershipLease_StillParses_AndIsGovernedByTheClock()
    {
        var path = NodeTypeBakeLease.PathFor(dir, Framework);
        var stale = DateTimeOffset.UtcNow - NodeTypeBakeLease.StaleAfter - TimeSpan.FromMinutes(1);
        File.WriteAllText(path, $"pod-old {stale.ToString("O", CultureInfo.InvariantCulture)}");

        using var successor = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-b");
        successor.Should().NotBeNull("an old two-field stamp is stale by its own instant");
    }

    /// <summary>
    /// An EMPTY lease file with a fresh file time is followed, never baked over. The realistic cause
    /// is a torn read — the heartbeat truncates and rewrites in place — so a live holder is beating,
    /// and taking the lease would put two pods on one compile.
    /// </summary>
    [Fact]
    public void AnUnrecognisableButFreshLease_IsFollowed()
    {
        File.WriteAllText(NodeTypeBakeLease.PathFor(dir, Framework), string.Empty);

        NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-b")
            .Should().BeNull("something wrote this file moments ago — that is a live holder");
    }

    /// <summary>
    /// …and the same file with a STALE time IS taken over, which repairs it. Nothing has written it in
    /// <see cref="NodeTypeBakeLease.StaleAfter"/>, so a torn read is impossible and a corrupt leftover
    /// must not wedge the fleet forever. That is why an unparseable stamp falls back to the file time
    /// rather than resolving straight to "follow".
    /// </summary>
    [Fact]
    public void AnUnrecognisableAndStaleLease_IsTakenOver_AndRepaired()
    {
        var path = NodeTypeBakeLease.PathFor(dir, Framework);
        File.WriteAllText(path, string.Empty);
        File.SetLastWriteTimeUtc(
            path, DateTime.UtcNow - NodeTypeBakeLease.StaleAfter - TimeSpan.FromMinutes(1));

        using var successor = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-b");

        successor.Should().NotBeNull("a corrupt leftover nobody is beating must never be permanent");
        File.ReadAllText(path).Should().StartWith("pod-b ", "taking it over writes a valid stamp");
    }

    /// <summary>
    /// Fail OPEN, but only for a broken SUBSTRATE. An unusable coordination directory must still let
    /// the pod bake: there is no fleet to coordinate with, and duplicate work is a cost while a fleet
    /// that never compiles is an outage.
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

    [Fact]
    public void HeartbeatInterval_IsWellInsideTheStaleWindow() =>
        NodeTypeBakeLease.HeartbeatInterval.Should()
            .BeLessThan(NodeTypeBakeLease.StaleAfter / 2,
                "on the no-cluster fallback path a single missed beat under load must not hand the "
                + "bake to a second pod while the first is still compiling");

    [Fact]
    public void DisposingTwice_IsHarmless()
    {
        var lease = NodeTypeBakeLease.TryAcquire(dir, Framework, "pod-a");
        lease.Should().NotBeNull();
        lease!.Dispose();
        lease.Dispose();
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>
    /// Back-date the heartbeat the way a crashed holder's would look — both the CONTENT instant (what
    /// the lease actually reads) and the file metadata (the fallback), so the test pins the decision
    /// rather than which of the two happened to be consulted.
    /// </summary>
    private void BackdateHeartbeat(TimeSpan by)
    {
        var path = NodeTypeBakeLease.PathFor(dir, Framework);
        var parts = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        parts[^1] = (DateTimeOffset.UtcNow - by).ToString("O", CultureInfo.InvariantCulture);
        File.WriteAllText(path, string.Join(' ', parts));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - by);
    }

    /// <summary>
    /// One pod's VIEW of the cluster: the members it can see running, the ones it has seen depart,
    /// and anything else — which is <see cref="ClusterMemberState.Unknown"/>, exactly as an
    /// un-hydrated Orleans snapshot answers for a silo it does not list.
    /// </summary>
    private sealed class FakeCluster(
        string localIdentity,
        IEnumerable<string>? live = null,
        IEnumerable<string>? gone = null) : IClusterMembership
    {
        private readonly ImmutableHashSet<string> live =
            (live ?? []).ToImmutableHashSet().Add(localIdentity);

        private readonly ImmutableHashSet<string> gone = (gone ?? []).ToImmutableHashSet();

        public string LocalIdentity { get; } = localIdentity;

        public ClusterMemberState StateOf(string identity) =>
            gone.Contains(identity) ? ClusterMemberState.Gone
            : this.live.Contains(identity) ? ClusterMemberState.Alive
            : ClusterMemberState.Unknown;
    }

    /// <summary>A membership service that is broken, not merely uninformed.</summary>
    private sealed class ThrowingCluster : IClusterMembership
    {
        public string LocalIdentity => throw new InvalidOperationException("membership is down");

        public ClusterMemberState StateOf(string identity) =>
            throw new InvalidOperationException("membership is down");
    }
}
