using System;
using System.Collections.Generic;
using MeshWeaver.Hosting.Security;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>#3017 — "did the OWNER answer, or did the routing layer?" must be DERIVED from the
/// producers, never from a list of the sentences somebody thought of.</b>
///
/// <para><b>What went wrong.</b>
/// <c>MeshWeaver.Hosting.Monolith.Test.SilentReadNackTest</c> asks that question of every NACK a
/// torn-down owner sends, and answered it by matching four hand-written shapes. A FIFTH owner-side
/// terminal — <see cref="AccessControlPipeline"/>'s refusal for a delivery whose access gate could
/// not run because the hub is gone — was rejected as "not from the owner" and reddened the suite on
/// a perfectly correct outcome. A SIXTH was already live and equally unlisted: the intake gate's
/// <see cref="ShutdownNack.RejectingNow"/>. The class's own guard passed throughout, because a
/// guard over an enumeration can only assert the members that were already written down.</para>
///
/// <para><b>What replaced it.</b> Every owner-side refusal is now COMPOSED by
/// <see cref="ShutdownNack"/>, so it opens with <see cref="ShutdownNack.Banner"/> by construction,
/// and <see cref="ShutdownNack.IsAnsweredByOwner"/> reads that same banner back. Recognition
/// follows the producers instead of trailing them: a seventh terminal is recognised the day it is
/// written, without anybody remembering to widen a list.</para>
///
/// <para><b>What this guard is, and is not.</b> It asserts no spelling — asserting the literal
/// would be the same defect one level up, a guard checking its own copy of the string. It CALLS
/// the real producers and runs the real predicate over what they actually say, exactly as
/// <c>TransientMarkerPairingGuard</c> does for the transient-marker contract. A producer whose
/// wording drifts off the seam fails here, whatever the new wording is.</para>
/// </summary>
public class OwnerAnswerRecognitionGuard(ITestOutputHelper output)
{
    private static readonly Address Owner = new("TestData", "silent-read");

    private static ObjectDisposedException DisposedScope() =>
        new("LifetimeScope",
            "Instances cannot be resolved and nested lifetimes cannot be created from this "
            + "LifetimeScope as it (or one of its parent scopes) has already been disposed.");

    /// <summary>
    /// Every owner-side refusal a real producer emits, built by CALLING that producer.
    /// </summary>
    private static IEnumerable<(string Producer, string Message)> OwnerAnswers()
    {
        // The access gate — the FIFTH terminal, and the one #3017 was filed for.
        yield return ("AccessControlPipeline.RecyclingRefusal(scope disposed)",
            AccessControlPipeline.RecyclingRefusal(Owner, "GetDataRequest", DisposedScope()));
        yield return ("AccessControlPipeline.RecyclingRefusal(undetermined fold)",
            AccessControlPipeline.RecyclingRefusal(Owner, "GetDataRequest",
                "the permission query could not run"));

        // The intake gate — the SIXTH, unlisted and unnoticed until the seam made it moot.
        yield return ("MessageService intake gate (ShutdownNack.RejectingNow)",
            ShutdownNack.RejectingNow(
                Owner,
                $"RunLevel=Dead, {ShutdownNack.ActivationMarker}DEADBEEF",
                "cannot process GetDataRequest"));

        // The queued turn that came too late.
        yield return ("MessageService late turn (ShutdownNack.RetryForTheAuthoritativeAnswer)",
            ShutdownNack.RetryForTheAuthoritativeAnswer(
                Owner,
                "RunLevel=Dead",
                "GetDataRequest (id=abc) was accepted before disposal began and its turn came "
                + "too late to process"));

        // The typed refusal a handler throws, both constructors — they build the sentence
        // independently of each other.
        yield return ("HubDisposingException(address, what)",
            new HubDisposingException(Owner, "the synchronization stream").Message);
        yield return ("HubDisposingException(address, what, inner)",
            new HubDisposingException(Owner, "the permission fold", DisposedScope()).Message);

        // The shape that actually reaches a consumer: the owner's answer flattened into a
        // DeliveryFailure message and relayed by the routing layer. Relayed is still the OWNER's
        // answer — the routing layer only carried it.
        yield return ("owner answer relayed by routing",
            $"Delivery to '{Owner}' failed: "
            + new HubDisposingException(Owner, "/data/'x'").Message);
    }

    public static TheoryData<string, string> ProducedOwnerAnswers()
    {
        var data = new TheoryData<string, string>();
        foreach (var (producer, message) in OwnerAnswers())
            data.Add(producer, message);
        return data;
    }

    [Theory]
    [MemberData(nameof(ProducedOwnerAnswers))]
    public void EveryOwnerSideRefusal_IsRecognisedAsTheOwnersAnswer(string producer, string message)
    {
        output.WriteLine($"{producer}: {message}");

        ShutdownNack.IsAnsweredByOwner(message, Owner).Should().BeTrue(
            $"{producer} speaks FOR the owner at {Owner}. If this fails the producer stopped "
            + "composing through ShutdownNack — restore that rather than widening the predicate "
            + "to whatever the new sentence says, which is the enumeration #3017 removed");
        ShutdownNack.IsAnsweredByOwner(message, Owner.Path).Should().BeTrue(
            "callers hold the owner as a PATH as often as an Address, and the two must answer "
            + "identically or the predicate is a coin toss on which one is at hand");

        MeshNodeStreamCache.IsTransientOwnerFailure(new InvalidOperationException(message))
            .Should().BeTrue(
                "the same sentence must also read TRANSIENT — a recognised owner answer that the "
                + "read path treats as terminal is #2727 in a new costume");
    }

    /// <summary>
    /// 🚨 The half that keeps the predicate a real check. The routing layer manufactures failures
    /// for the SAME address, and two of them even contain "is shutting down" — but none of them
    /// makes THIS address the subject of it, which is the whole discrimination.
    /// </summary>
    [Theory]
    [InlineData("No node found at 'TestData/silent-read' — the node was deleted, so this address "
                + "will not reactivate.", "a routing NotFound is a provable ABSENCE, not a recycling owner")]
    [InlineData("No route to 'TestData/silent-read'",
        "a bare routing failure promises no retry and names no owner")]
    [InlineData("Mesh is shutting down, cannot route to TestData/silent-read",
        "the MESH going down is not this owner answering — the subject of 'is shutting down' is "
        + "the mesh, and it says nothing about whether this address reactivates")]
    [InlineData("Host is shutting down, cannot route to TestData/silent-read",
        "same for the Orleans host — an ErrorType-only predicate would have accepted this, which "
        + "is why the banner and not the classification is the evidence")]
    [InlineData("Hub TestData/silent-read/child is shutting down; retry to get the authoritative answer.",
        "a DIFFERENT hub's recycle is not this owner answering")]
    [InlineData("Hub TestData/silent-rea is shutting down — cannot process GetDataRequest.",
        "a PREFIX of the owner's path is a different address")]
    public void ARoutingLayerFailure_IsNotTheOwnersAnswer(string message, string because)
    {
        ShutdownNack.IsAnsweredByOwner(message, Owner).Should().BeFalse(because);
        ShutdownNack.IsAnsweredByOwner(message, Owner.Path).Should().BeFalse(because);
    }

    /// <summary>
    /// The producers are enumerated by hand, so the enumeration itself can silently empty out —
    /// a Theory with no cases PASSES. Assert there are cases, and that each says something.
    /// </summary>
    [Fact]
    public void TheProducerSetIsNotEmpty()
    {
        OwnerAnswers().Should().HaveCountGreaterThanOrEqualTo(7,
            "a Theory with no data passes having tested nothing — the one failure mode a "
            + "recognition guard must not have");
        OwnerAnswers().Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Message));
    }

    /// <summary>
    /// 🚨 And the predicate must be able to say NO at all — a rubber stamp would make every
    /// assertion above vacuous.
    /// </summary>
    [Fact]
    public void WithTheBannerRemoved_ThePredicateRefuses()
    {
        ShutdownNack.IsAnsweredByOwner(
            $"Hub {Owner} has stopped — cannot process GetDataRequest.", Owner)
            .Should().BeFalse(
                "if this passes the predicate accepts anything that names the address, and every "
                + "assertion above is vacuous");
        ShutdownNack.IsAnsweredByOwner(null, Owner).Should().BeFalse("no message is no answer");
        ShutdownNack.IsAnsweredByOwner(string.Empty, Owner).Should().BeFalse("nor is an empty one");
    }
}
