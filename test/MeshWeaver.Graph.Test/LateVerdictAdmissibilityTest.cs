using System;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The late-verdict watch must answer two questions the owner side used to ASSUME — issue #3197.
///
/// <para><b>The assumption.</b> The owner's ack watcher deliberately posts NOTHING when the owner is
/// shutting down, deferring the verdict to the ShutDown-phase disposal NACK. Its justification —
/// and the justification for <c>LateResponseWatchBound</c> itself — enumerated the owner-side paths
/// this window has to dominate, including "the disposal NACK after the owner's phased teardown
/// (hosted-hub drain capped at 5 s)". <b>That cap was deleted in #1317</b>:
/// <c>HostedHubsCollection.DisposeHubsReactive</c> carries no <c>Timeout</c>, and the disposal
/// watchdog that remains is a STALL detector re-armed on every <c>RunLevel</c> transition in the
/// subtree, so a subtree making steady progress never trips it. The stand-aside was therefore
/// waiting on a route with no duration bound, and could not check that the route existed at all.</para>
///
/// <para><b>And the drop was silent.</b> <c>Dispatch</c> removed the entry, saw it was expired, and
/// returned <c>false</c> — the identical answer it gives for a request nobody ever armed. So a
/// failing run showed <c>VERDICT_TIMEOUT</c> with ZERO late-terminal records, and "the owner never
/// produced a verdict" and "the owner produced one that arrived too late" were indistinguishable
/// (measured, #2543). Two different investigations, one symptom.</para>
///
/// <para>These pins are over the registry alone — plain dictionary state, no mesh, no hub.</para>
/// </summary>
public class LateVerdictAdmissibilityTest
{
    private const string RequestId = "patch-1";
    private const string Path = "acme/Doc";

    /// <summary>
    /// A clock the test moves by hand. The window is 30 s and no test waits it out — expiry is
    /// reached by advancing this, never by a sleep.
    /// </summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    /// <summary>An armed, unexpired watch is admissible — so standing aside for it is justified.</summary>
    [Fact]
    public void AnArmedWatch_IsAdmissible()
    {
        var registry = new LatePatchResponseRegistry();
        registry.Register(RequestId, Path, _ => { }, _ => { });

        registry.IsAdmissible(RequestId).Should().BeTrue();
    }

    /// <summary>
    /// 🚨 Nobody armed: standing aside would convert an answerable write into silence, so the
    /// predicate says no and the ack watcher answers on whatever transport is still open. This is
    /// the cross-process caller, and the no-sink fixture.
    /// </summary>
    [Fact]
    public void AnUnarmedRequest_IsNotAdmissible()
        => new LatePatchResponseRegistry().IsAdmissible(RequestId).Should().BeFalse();

    /// <summary>
    /// A watch the caller's own bounded wait already settled is not admissible either — its
    /// verdict has been delivered, and there is nothing left to defer to.
    /// </summary>
    [Fact]
    public void ACompletedWatch_IsNotAdmissible()
    {
        var registry = new LatePatchResponseRegistry();
        registry.Register(RequestId, Path, _ => { }, _ => { });
        registry.Complete(RequestId);

        registry.IsAdmissible(RequestId).Should().BeFalse();
    }

    /// <summary>
    /// 🚨 THE REGRESSION. A verdict past the window is still NOT delivered — acting on it is the bug
    /// the bound exists to prevent — but it is now COUNTED, so "arrived too late" stops looking
    /// exactly like "never produced".
    /// </summary>
    [Fact]
    public void AnExpiredVerdict_IsNotDelivered_ButIsCounted()
    {
        var clock = new TestClock();
        var registry = new LatePatchResponseRegistry(clock: clock);
        var delivered = 0;
        registry.Register(RequestId, Path, _ => delivered++, _ => { });
        clock.Advance(LatePatchResponseRegistry.LateResponseWatchBound + TimeSpan.FromSeconds(1));

        var dispatched = registry.Dispatch(RequestId, new PatchDataResponse(false, 1L));

        dispatched.Should().BeFalse("a verdict past the window is indistinguishable from a stale one");
        delivered.Should().Be(0, "and must not reach the caller");
        registry.ExpiredVerdicts.Should().Be(1, "but it must not vanish without a trace either");
    }

    /// <summary>The #2661 failure seam carries the identical rule.</summary>
    [Fact]
    public void AnExpiredFailure_IsNotDelivered_ButIsCounted()
    {
        var clock = new TestClock();
        var registry = new LatePatchResponseRegistry(clock: clock);
        var delivered = 0;
        registry.Register(RequestId, Path, _ => { }, _ => delivered++);
        clock.Advance(LatePatchResponseRegistry.LateResponseWatchBound + TimeSpan.FromSeconds(1));

        registry.DispatchFailure(RequestId, new DeliveryFailure(null!)).Should().BeFalse();
        delivered.Should().Be(0);
        registry.ExpiredVerdicts.Should().Be(1);
    }

    /// <summary>
    /// 🚨 The control arm, and the discriminator the counter exists for: a request NOBODY armed is
    /// also `false` from Dispatch — and must NOT be counted as expired, or the two cases collapse
    /// again in the other direction.
    /// </summary>
    [Fact]
    public void AnUnarmedRequest_IsNotCountedAsExpired()
    {
        var registry = new LatePatchResponseRegistry();

        registry.Dispatch(RequestId, new PatchDataResponse(false, 1L)).Should().BeFalse();

        registry.ExpiredVerdicts.Should().Be(0,
            "nothing was produced late here — nothing was produced at all");
    }

    /// <summary>Control arm: an unexpired verdict is delivered and is never counted as expired, so
    /// the assertions above cannot be green because dispatch stopped working.</summary>
    [Fact]
    public void AnInTimeVerdict_IsDelivered_AndNotCounted()
    {
        var registry = new LatePatchResponseRegistry();
        var delivered = 0;
        registry.Register(RequestId, Path, _ => delivered++, _ => { });

        registry.Dispatch(RequestId, new PatchDataResponse(true, 1L)).Should().BeTrue();
        delivered.Should().Be(1);
        registry.ExpiredVerdicts.Should().Be(0);
    }

}
