using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Hosting;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>#2795 — the "is shutting down" marker is a contract between many producers and two
/// classifiers, and nothing pinned the pairing.</b>
///
/// <para><c>MeshNodeStreamCache.IsTransientOwnerFailure</c> and
/// <see cref="AreaErrorClassifier.IsTransientHubFailure"/> decide "the owner went away, ask again"
/// by SUBSTRING-MATCHING an exception message. Across <c>src/</c> there are 53 live occurrences of
/// <c>is shutting down</c>; every one is a producer in that contract. A reworded NACK silently
/// stops being classified as transient, and the caller then treats a teardown as a real answer.</para>
///
/// <para><b>This has already been paid for once.</b> From #2727's disposition, measured at 100 runs
/// a branch: <c>main</c> 20 failures/100; the fix's first draft <b>10</b> — <i>"the rethrow worked,
/// but I had reworded the NACK and dropped the 'shutting down' marker IsTransientOwnerFailure keys
/// on; all 10 were that regression"</i>; the marker restored, 1. <b>Half the remaining failures in
/// that draft were a broken marker pairing</b>, and it was caught only because someone happened to
/// be running a 100× flake repro. Ordinary CI would have shipped it, and it would have presented as
/// an unrelated intermittent somewhere else entirely.</para>
///
/// <para><b>What this guard is, and is not.</b> It does not assert a spelling — asserting the
/// literal would be the same defect one level up, a guard that checks its own copy of the string.
/// It CONSTRUCTS the real producers and runs the real classifiers over what they actually say. A
/// producer whose wording drifts fails here, whatever the new wording is, because the classifier
/// stops recognising it.</para>
///
/// <para>It also pins the two classifiers to each other. They are separate substring lists in
/// separate assemblies — <c>MeshWeaver.Hosting</c> and <c>MeshWeaver.Layout</c> — and the mesh's
/// behaviour only makes sense while they agree: the cache riding a fault out while the GUI treats
/// it as terminal (or the reverse) is a split brain nothing else reports.</para>
/// </summary>
public class TransientMarkerPairingGuard
{
    private static readonly Address DisposingAddress = new("test", "disposing-node");

    /// <summary>
    /// Every message a real producer of the teardown marker emits, built by CALLING the producer.
    /// <see cref="HubDisposingException"/> is the documented central one — its own remarks say the
    /// wording is load-bearing for exactly these two classifiers — and both constructors are
    /// covered because they build the sentence independently.
    /// </summary>
    public static TheoryData<string, string> ProducedMessages()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, message) in Produced())
            data.Add(name, message);
        return data;
    }

    private static IEnumerable<(string Name, string Message)> Produced()
    {
        yield return ("HubDisposingException(address, what)",
            new HubDisposingException(DisposingAddress, "the synchronization stream").Message);

        yield return ("HubDisposingException(address, what, inner)",
            new HubDisposingException(
                DisposingAddress, "the permission fold",
                new ObjectDisposedException("LifetimeScope")).Message);

        // The shape that actually reaches the cache: the typed exception has been flattened into a
        // DeliveryFailure's message on the way across a hub (or silo) boundary, so only the TEXT
        // survives. This is the case the classifiers exist for.
        yield return ("DeliveryFailure carrying a flattened HubDisposingException",
            $"Delivery to '{DisposingAddress}' failed: "
            + new HubDisposingException(DisposingAddress, "/data/'x'").Message);
    }

    [Theory]
    [MemberData(nameof(ProducedMessages))]
    public void EveryProducedTeardownMessage_IsClassifiedTransientByBothClassifiers(
        string producer, string message)
    {
        var error = new InvalidOperationException(message);

        MeshNodeStreamCache.IsTransientOwnerFailure(error).Should().BeTrue(
            $"{producer} announces a teardown, and MeshNodeStreamCache must ride it out rather than "
            + "record a negative (missing-node) entry for an address that may reactivate. If this "
            + "fails, the producer's wording drifted away from the classifier — do not 'fix' it by "
            + "widening the classifier to whatever the new sentence says (#2795)");

        AreaErrorClassifier.IsTransientHubFailure(error).Should().BeTrue(
            $"{producer} must read the same way to the GUI as it does to the cache. The two lists "
            + "live in different assemblies and only make sense while they agree: one riding a "
            + "fault out while the other treats it as terminal is a split brain nothing reports");
    }

    /// <summary>
    /// 🚨 The guard's own failure mode. Everything above would still pass if the classifiers said
    /// "transient" to ANY message — which is precisely how a marker check rots into a rubber stamp.
    /// Strip the marker and both must refuse.
    /// </summary>
    [Fact]
    public void WithTheMarkerRemoved_BothClassifiersRefuse()
    {
        var withoutMarker = new InvalidOperationException(
            $"Hub {DisposingAddress} has stopped — cannot create \"the synchronization stream\".");

        MeshNodeStreamCache.IsTransientOwnerFailure(withoutMarker).Should().BeFalse(
            "if this passes, the classifier accepts anything and every assertion above is vacuous");
        AreaErrorClassifier.IsTransientHubFailure(withoutMarker).Should().BeFalse(
            "same, for the GUI half");
    }

    /// <summary>
    /// The producers are enumerated by hand, so the enumeration itself can silently empty out — a
    /// Theory with no cases PASSES. Assert there are cases, and that they are the ones intended.
    /// </summary>
    [Fact]
    public void TheProducerSetIsNotEmpty()
    {
        Produced().Should().HaveCountGreaterThanOrEqualTo(3,
            "a Theory with no data passes having tested nothing — the one failure mode a pairing "
            + "guard must not have");
        Produced().Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Message));
    }
}
