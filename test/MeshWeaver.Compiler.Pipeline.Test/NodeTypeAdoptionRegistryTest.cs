#pragma warning disable CS1591

using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The adoption interlock (#1763): a first-build kickoff must WAIT for an adoption that is already
/// in flight instead of racing it — and it must never wait for one that is not.
///
/// <para>Pure: no hub, no mesh. The registry is the whole ordering argument, so it is testable on
/// its own, and the ways it can go wrong are all cheap to state — a release that is missed costs
/// the caller its full wait budget, and a reservation that reads as clear costs the adoption.</para>
/// </summary>
public class NodeTypeAdoptionRegistryTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public void NoReservation_IsClearImmediately()
    {
        var registry = new NodeTypeAdoptionRegistry();
        Assert.False(registry.IsReserved("Widget/Thing"));
        // No timeout wrapper: if this ever has to wait, the test should hang loudly rather than
        // report a tidy failure that reads like a flake.
        registry.WhenClear("Widget/Thing").Timeout(Budget).Wait();
    }

    [Fact]
    public async Task AReservationHoldsTheWait_UntilItIsReleased()
    {
        var registry = new NodeTypeAdoptionRegistry();
        var reservation = registry.Reserve("Widget/Thing");
        Assert.True(registry.IsReserved("Widget/Thing"));

        // 🚨 An AsyncSubject the subscription completes, not a hand-woven gate — both waits below
        // are reactive assertions on it, so nothing parks a thread.
        var cleared = new AsyncSubject<Unit>();
        using var subscription = registry.WhenClear("Widget/Thing")
            .Subscribe(_ =>
            {
                cleared.OnNext(Unit.Default);
                cleared.OnCompleted();
            });

        await cleared.Should().NotEmit(200.Milliseconds(),
            "the kickoff must not proceed while an adoption is in flight — that is the whole race");

        reservation.Dispose();
        await cleared.Should().Within(Budget).Emit(
            "the kickoff must proceed once the adoption released its reservation; a missed release "
            + "costs the caller its ENTIRE wait budget, per type");
        Assert.False(registry.IsReserved("Widget/Thing"));
    }

    /// <summary>
    /// 🚨 The subscribe-vs-check ordering. A caller that tests <c>IsReserved</c> and only then
    /// subscribes loses a release that lands in between and pays the full budget for a reservation
    /// that is already gone. Here the release happens BEFORE the subscription exists, so the only
    /// thing that can answer is the re-check leg — which is the leg that would be missing if the
    /// two were ordered the other way round.
    /// </summary>
    [Fact]
    public void AReleaseThatLandedBeforeTheSubscription_IsNotLost()
    {
        var registry = new NodeTypeAdoptionRegistry();
        registry.Reserve("Widget/Thing").Dispose();

        registry.WhenClear("Widget/Thing").Timeout(Budget).Wait();
    }

    /// <summary>
    /// Two bundles can legitimately carry the same node path. One of them finishing must not tell
    /// the kickoff that the other is done — otherwise the interlock protects only the first
    /// adoption and the second races exactly as before.
    /// </summary>
    [Fact]
    public async Task ReservationsAreReferenceCounted()
    {
        var registry = new NodeTypeAdoptionRegistry();
        var first = registry.Reserve("Widget/Thing");
        var second = registry.Reserve("Widget/Thing");

        var cleared = new AsyncSubject<Unit>();
        using var subscription = registry.WhenClear("Widget/Thing")
            .Subscribe(_ =>
            {
                cleared.OnNext(Unit.Default);
                cleared.OnCompleted();
            });

        first.Dispose();
        Assert.True(registry.IsReserved("Widget/Thing"));
        await cleared.Should().NotEmit(200.Milliseconds(),
            "one of two in-flight adoptions finishing must not release the path");

        second.Dispose();
        await cleared.Should().Within(Budget).Emit();
    }

    /// <summary>A reservation on one path says nothing about another.</summary>
    [Fact]
    public void AReservationIsScopedToItsPath()
    {
        var registry = new NodeTypeAdoptionRegistry();
        using var held = registry.Reserve("Widget/Thing");

        Assert.False(registry.IsReserved("Widget/Other"));
        registry.WhenClear("Widget/Other").Timeout(Budget).Wait();
    }

    /// <summary>Disposing a handle twice must not release someone else's reservation.</summary>
    [Fact]
    public void DisposingAHandleTwice_ReleasesOnce()
    {
        var registry = new NodeTypeAdoptionRegistry();
        var first = registry.Reserve("Widget/Thing");
        using var second = registry.Reserve("Widget/Thing");

        first.Dispose();
        first.Dispose();

        Assert.True(registry.IsReserved("Widget/Thing"),
            "the second reservation is still in flight — a double dispose must not clear it");
    }
}
