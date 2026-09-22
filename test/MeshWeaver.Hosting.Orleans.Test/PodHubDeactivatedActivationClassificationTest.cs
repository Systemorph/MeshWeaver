using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #2299 — the deactivated-activation arm existed, was correct, and was INERT on the one
/// leg the fault lives on.</b>
///
/// <para><c>RoutingGrain.IsDeactivatedActivation</c> was added for Orleans' rejection
/// <c>… after "DeactivateOnIdle was called." to invalid activation. Rejecting now.</c>, and it is
/// gated on a discriminator: <c>activationErrorRecorded</c> being FALSE is what separates
/// "deactivated on idle, will be back" from a per-node hub that "cannot activate at all". That
/// parameter DEFAULTS to <c>true</c> — assume the worse case — so any caller that cannot consult
/// <c>GrainActivationFailureRegistry</c> leaves the verdict terminal. <c>BuildPodHubRoute</c> passed
/// the default, so on the pod-hub leg the arm could never fire and the shape kept being reported as
/// <see cref="ErrorType.Failed"/> — which is what production printed verbatim
/// (<c>[ROUTE] Directed delivery to pod hub … failed — surfacing <b>Failed</b> DeliveryFailure to
/// sender …</c>), for 947 occurrences and for every one of the newest samples.</para>
///
/// <para><b>What makes <c>false</c> a FACT on this leg rather than a guess.</b> The registry holds
/// the last activation failure "for each per-node-hub grain" and is written only by
/// <c>MessageHubGrain</c>. A <c>PodHubGrain</c> has no NodeType, no hub configuration to
/// materialise, and an <c>OnActivateAsync</c> that deliberately never throws — its refusal is the
/// CALL's answer (<c>PodHubNotHereException</c>) — so it neither records an activation error nor
/// could. <c>RoutingGrain.ClassifyPodHubDeliveryException</c> states that once, and this class pins
/// it from both sides.</para>
///
/// <para><b>Why no cluster is needed.</b> Both classifiers are pure <c>internal static</c>
/// functions and every rejection text below is quoted VERBATIM from the production log, so a reader
/// can see exactly what is matched. The facts are deliberately paired: each one that asserts the
/// corrected verdict has a sibling asserting the answer the OTHER side of the discriminator still
/// gives, because a single fact about one member cannot see a rule that CHOOSES between members.</para>
/// </summary>
public class PodHubDeactivatedActivationClassificationTest
{
    /// <summary>
    /// The exception the router actually receives. Orleans keeps
    /// <see cref="OrleansMessageRejectionException"/>'s constructors internal, so this reaches the
    /// <c>(string message)</c> one by reflection rather than substituting a base-class stand-in — the
    /// arm under test reads the MESSAGE, so <c>GetUninitializedObject</c> (which yields none) would
    /// make every fact here vacuous. Construction cannot fail silently: a removed constructor throws
    /// <see cref="MissingMethodException"/>, and the self-check fails loudly if the message is lost.
    /// </summary>
    /// <param name="message">The rejection text, verbatim from production.</param>
    /// <returns>A real rejection carrying that message.</returns>
    private static Exception Rejection(string message)
    {
        var rejection = (Exception)Activator.CreateInstance(
            typeof(OrleansMessageRejectionException),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: [message],
            culture: null)!;

        rejection.Message.Should().Be(message,
            "every fact in this class turns on the rejection's MESSAGE, so a construction that "
            + "silently dropped it would let them pass having classified something else");

        return rejection;
    }

    /// <summary>
    /// Verbatim from <c>Admin/_LogIncident/e849e4a7795e0c92</c> (#2299 body, sample of
    /// 2026-08-23 21:16:56Z) — the oldest of the two, a <c>cache/…</c> pod hub.
    /// </summary>
    private const string OriginalForwardingRejectionText =
        "Forwarding failed: tried to forward message Request "
        + "[S10.244.3.146:11111:146520573 sys.client/hosted-10.244.3.146:11111@146520573]->"
        + "[S10.244.3.146:11111:146520573 podhub/cache/EYgshhMBE0CsSP9e2xj-Pw] "
        + "MeshWeaver.Connection.Orleans.IPodHubGrain.Deliver(MeshWeaver.Messaging.IMessageDelivery) "
        + "#72B6637C56CD5365[ForwardCount=2] for 2 times after \"DeactivateOnIdle was called.\" to "
        + "invalid activation. Rejecting now. ";

    /// <summary>
    /// Verbatim from #2299's newest recurrence fold (sample of 2026-09-21 12:13:24Z) — the same shape
    /// a month later, on a different pod generation, which is what says the classification half was
    /// never reached rather than merely deployed late.
    /// </summary>
    private const string NewestForwardingRejectionText =
        "Forwarding failed: tried to forward message Request "
        + "[S10.244.3.104:11111:148993377 sys.client/hosted-10.244.3.104:11111@148993377]->"
        + "[S10.244.3.104:11111:148993377 podhub/cache/OF-_1OFvNkOxMFqzIG3gyQ] "
        + "MeshWeaver.Connection.Orleans.IPodHubGrain.Deliver(MeshWeaver.Messaging.IMessageDelivery) "
        + "#678040A3C443D132[ForwardCount=2] for 2 times after \"DeactivateOnIdle was called.\" to "
        + "invalid activation. Rejecting now. ";

    /// <summary>
    /// The corrected verdict on the leg the evidence comes from. A <c>PodHubGrain</c> that answered
    /// "not here" requested its own idle deactivation, so a rejection about its invalid activation is
    /// a lifecycle transition by construction and the target answers again on its next claim.
    ///
    /// <para>Reverting <c>BuildPodHubRoute</c> to the general classifier turns this red.</para>
    /// </summary>
    /// <param name="rejectionText">One of the two production samples.</param>
    [Theory]
    [InlineData(OriginalForwardingRejectionText)]
    [InlineData(NewestForwardingRejectionText)]
    public void ADeactivatedPodHubActivation_IsNackedAsTransient(string rejectionText)
    {
        RoutingGrain.ClassifyPodHubDeliveryException(Rejection(rejectionText))
            .Should().Be(ErrorType.ShuttingDown,
                "the pod-hub activation this message was forwarded to was deactivating — it asked "
                + "for that itself when it answered 'not here', and the address is served again on "
                + "its next claim. Told terminally, the consumers that carry their own recovery "
                + "machinery tear down instead of riding it out, which on the newest #2299 sample "
                + "ended an agent round");
    }

    /// <summary>
    /// 🚨 <b>The other side of the same rejection, and the reason the fix is a WIRING change rather
    /// than a predicate change.</b> The general classifier, asked with its own defaults — exactly
    /// what <c>BuildPodHubRoute</c> used to pass — still answers terminally for the identical
    /// exception. That is the pre-fix production verdict, reproduced here, and it is what makes the
    /// fact above a statement about the LEG instead of a statement about the predicate.
    /// </summary>
    /// <param name="rejectionText">One of the two production samples.</param>
    [Theory]
    [InlineData(OriginalForwardingRejectionText)]
    [InlineData(NewestForwardingRejectionText)]
    public void TheGeneralClassifierWithItsDefaults_StillAnswersTerminally(string rejectionText)
    {
        RoutingGrain.ClassifyDeliveryException(Rejection(rejectionText))
            .Should().Be(ErrorType.Failed,
                "'assume the worse case' is the right default for a caller that cannot consult the "
                + "activation-failure registry, and it is the answer the pod-hub leg was getting — "
                + "so this is the defect, reproduced, and it must stay the answer for callers that "
                + "genuinely cannot tell the two conditions apart");
    }

    /// <summary>
    /// 🚨 <b>The discriminator is load-bearing, asserted over BOTH of its values.</b> For one and the
    /// same rejection the verdict must flip with <c>activationErrorRecorded</c> alone: a per-node hub
    /// whose real activation error IS recorded is in a persistent fault loop that never recovers, and
    /// reporting that as a lifecycle transition would hide a broken NodeType compile behind a
    /// transient NACK. Collapsing <c>IsDeactivatedActivation</c> into an unconditional text match
    /// turns this red.
    /// </summary>
    [Fact]
    public void TheRecordedActivationError_IsWhatChoosesBetweenTheTwoVerdicts()
    {
        var rejection = Rejection(NewestForwardingRejectionText);

        RoutingGrain.ClassifyDeliveryException(rejection, scopeDisposed: null,
                activationErrorRecorded: false)
            .Should().Be(ErrorType.ShuttingDown,
                "no activation error recorded for the grain ⇒ it deactivated on idle and will be back");

        RoutingGrain.ClassifyDeliveryException(rejection, scopeDisposed: null,
                activationErrorRecorded: true)
            .Should().Be(ErrorType.Failed,
                "an activation error IS recorded ⇒ the grain cannot activate at all, which is a "
                + "defect to report rather than a transition to ride out");
    }

    /// <summary>
    /// 🚨 <b>The pod-hub classifier is not blanket-transient.</b> It fixes ONE default; every other
    /// arm is evaluated unchanged, so an <see cref="OrleansMessageRejectionException"/> that carries
    /// none of the recognised phrases stays terminal on this leg too — "anything this does not
    /// recognise stays terminal, so a genuine defect is still reported as one".
    /// </summary>
    [Fact]
    public void ARejectionWithNoRecognisedShape_StaysTerminalOnThePodHubLegToo()
    {
        RoutingGrain.ClassifyPodHubDeliveryException(
                Rejection("Grain extension not installed on target grain."))
            .Should().Be(ErrorType.Failed,
                "the rejection type says nothing about WHY Orleans refused; widening this leg to "
                + "accept the type alone would demote real refusals on the busiest routing path");
    }

    /// <summary>
    /// The arms that already worked on this leg must keep working: a departed silo incarnation — the
    /// other of #2299's two production shapes — is still transient through the pod-hub classifier.
    /// A seam that answered only about its own arm would pass the facts above while regressing this.
    /// </summary>
    [Fact]
    public void ADepartedSiloRejection_IsStillTransientThroughThePodHubClassifier()
    {
        RoutingGrain.ClassifyPodHubDeliveryException(Rejection(
                "Exception while sending message: "
                + "Orleans.Runtime.Messaging.ConnectionFailedException: Unable to connect to "
                + "S10.244.4.87:11111:146498551, will retry after 585.8766ms"))
            .Should().Be(ErrorType.ShuttingDown,
                "#2299's first shape is classified by IsDepartedSiloRejection and must be unaffected "
                + "by the deactivated-activation default this change corrects");
    }

    /// <summary>
    /// 🚨 <b>THE WIRING, not just the decision — review on #5174.</b> The facts above are about the
    /// two classifiers, so reverting the one line in <c>BuildPodHubRoute</c> that chooses between
    /// them would leave them all green: the same shape as the defect being fixed, an argument not
    /// written at a call site. <c>TerminalCallFailure</c> is therefore a one-line delegation to
    /// <c>AnswerPodHubCallFailure</c>, and this drives that function — the SAME code production runs
    /// — capturing what it hands the sender.
    ///
    /// <para>Putting <c>ClassifyDeliveryException(ex, scopeDisposed)</c> back inside it flips the
    /// captured verdict to <see cref="ErrorType.Failed"/> and turns this red.</para>
    /// </summary>
    [Fact]
    public void ThePodHubTerminalArm_HandsTheSenderTheTransientVerdict()
    {
        var captured = new List<(string Message, ErrorType ErrorType)>();
        var logged = new List<LogLevel>();
        var delivery = new MessageDelivery<string>();

        RoutingGrain.AnswerPodHubCallFailure(
                Rejection(NewestForwardingRejectionText),
                "cache/OF-_1OFvNkOxMFqzIG3gyQ",
                delivery,
                (message, errorType) => captured.Add((message, errorType)),
                scopeDisposed: null,
                new RecordingLogger(logged))
            .Subscribe();

        captured.Should().ContainSingle(
                "the arm answers the sender exactly once — a delivery that faulted must never be "
                + "left waiting out its budget in silence")
            .Which.ErrorType.Should().Be(ErrorType.ShuttingDown,
                "this is the WIRING fact: the leg must reach the pod-hub classifier, and the general "
                + "one with its defaults answers Failed for this very exception (asserted above)");

        captured[0].Message.Should().Contain("cache/OF-_1OFvNkOxMFqzIG3gyQ",
            "the NACK names the address the sender could not reach");
    }

    /// <summary>
    /// 🚨 <b>The LEVEL follows the verdict, and it was equally unpinned.</b> A transient lifecycle
    /// transition reported at <see cref="LogLevel.Error"/> is what files an incident for a pod that
    /// was merely finishing (#2638) — and it is what kept #2299 collecting recurrences. Asserted on
    /// both sides so the level cannot be pinned to one value: an unrecognised fault is still
    /// <see cref="LogLevel.Error"/>, because it is still a defect to report.
    /// </summary>
    [Fact]
    public void TheReportedLevel_FollowsTheVerdict_OnBothSides()
    {
        var transient = new List<LogLevel>();
        var terminal = new List<LogLevel>();

        RoutingGrain.AnswerPodHubCallFailure(
                Rejection(NewestForwardingRejectionText), "cache/x", new MessageDelivery<string>(),
                (_, _) => { }, scopeDisposed: null, new RecordingLogger(transient))
            .Subscribe();

        RoutingGrain.AnswerPodHubCallFailure(
                Rejection("Grain extension not installed on target grain."), "cache/x",
                new MessageDelivery<string>(),
                (_, _) => { }, scopeDisposed: null, new RecordingLogger(terminal))
            .Subscribe();

        transient.Should().Equal([LogLevel.Information],
            "a lifecycle transition is not an incident to page on, and reporting it at Error is what "
            + "kept this fingerprint collecting recurrences");
        terminal.Should().Equal([LogLevel.Error],
            "an unrecognised fault on this leg is still a defect, and must still be reported as one");
    }

    /// <summary>
    /// Records the level of every entry written, so a fact can assert the level the arm CHOSE rather
    /// than merely that it logged. Deliberately minimal — it is a recorder, not a stand-in for a
    /// logging framework.
    /// </summary>
    /// <param name="levels">The list every logged level is appended to.</param>
    private sealed class RecordingLogger(List<LogLevel> levels) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => levels.Add(logLevel);
    }

    /// <summary>
    /// 🚨 <b>The container probe must be FORWARDED, not dropped.</b> <c>IsScopeTeardown</c> answers
    /// <c>false</c> without a probe, so a seam that forgot to pass <paramref name="scopeDisposed"/>
    /// on would silently re-open #2638 for this leg — a pod that was merely finishing, reported as a
    /// terminal routing failure. Asserted on both sides of the probe so the parameter cannot be
    /// inert.
    /// </summary>
    [Fact]
    public void TheScopeTeardownProbe_IsForwardedThroughThePodHubClassifier()
    {
        var disposed = new ObjectDisposedException("AutofacServiceProvider");

        RoutingGrain.ClassifyPodHubDeliveryException(disposed, scopeDisposed: () => true)
            .Should().Be(ErrorType.ShuttingDown,
                "the container is gone — this process is exiting and the delivery is being retried "
                + "against a live pod (#2638)");

        RoutingGrain.ClassifyPodHubDeliveryException(disposed, scopeDisposed: () => false)
            .Should().Be(ErrorType.Failed,
                "an unrelated disposed dependency IS a genuine defect, so only a probe that finds "
                + "the CONTAINER no longer resolving may demote it");
    }
}
