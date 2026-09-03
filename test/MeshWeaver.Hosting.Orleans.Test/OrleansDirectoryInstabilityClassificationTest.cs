using System;
using System.IO;
using System.Reactive.Concurrency;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #1742 / #2357 — the reply leg's remaining silent-loss mechanism, and it is a
/// CLASSIFIER, not a transport.</b>
///
/// <para>The pod-hub transport swap took cross-silo delivery to a pod-process hub off the Orleans
/// stream and onto a directed grain call, using Orleans' OWN grain directory as the address→silo
/// map. That closed the "a publish to nobody succeeds" mechanism — and made the reply leg depend on
/// the grain directory, which is precisely the thing that is UNSTABLE during a rolling deploy. Same
/// window, new dependency.</para>
///
/// <para><b>What the router did with it, and why nothing saw it.</b> When Orleans cannot address a
/// call it rejects the message with <c>RejectionTypes.Unrecoverable</c> <i>and the causing exception
/// attached</i>, and the caller-side resolution
/// (<c>CallbackData.HandleRejectionResponse</c>) is
/// <c>rejection?.Exception ?? new OrleansMessageRejectionException(…)</c> — <b>the carried exception
/// WINS</b>. So the router does not receive the
/// <see cref="OrleansMessageRejectionException"/> its classifier pattern-matches; it receives the
/// BARE <see cref="OrleansException"/> that <c>LocalGrainDirectory.LookupAsync</c> threw, whose own
/// text ends with the words <i>"Retry later."</i>. <c>RoutingGrain.IsTransientFailure</c> matched
/// neither, so the delivery was never retried, and both exception arms in the router then NACK'd the
/// sender with a hard-coded terminal <c>ErrorType.Failed</c>.</para>
///
/// <para>Both halves matter to a caller. The missing retry costs the message; the terminal
/// classification costs the SUBSCRIPTION — <c>SynchronizationStream</c>'s resubscribe latch and
/// <c>MeshNodeStreamCache</c>'s transient-owner rule ride out
/// <see cref="ErrorType.ShuttingDown"/> and tear down on <see cref="ErrorType.Failed"/>, so a
/// membership transition permanently killed mirrors that would have resumed seconds later. Prod:
/// 16 occurrences across two rolling deploys, naming <c>IPodHubGrain.Deliver</c> and
/// <c>IMessageHubGrain.DeliverMessage</c> among the dropped targets (#2357).</para>
///
/// <para>These facts are pure and need no cluster — the two classifiers and the retry primitive are
/// <c>internal static</c>, and the exception texts are quoted verbatim from the production log.</para>
/// </summary>
public class OrleansDirectoryInstabilityClassificationTest
{
    private static readonly Func<int, TimeSpan> NoBackoff = _ => TimeSpan.Zero;

    /// <summary>
    /// Verbatim from the production log (#2357) — the directory-handoff variant, i.e. the local silo
    /// is no longer the owner of the entry and the forward chain ran out of hops.
    /// </summary>
    private const string HopLimitText =
        "Silo S10.244.4.87:11111:146498551 is not owner of messagehub/LocalSyncDemo/_Sync/party-1, "
        + "cannot forward LookUpAsync to owner S10.244.3.44:11111:146515001 because hop limit is reached";

    /// <summary>
    /// Verbatim from Orleans' own <c>LocalGrainDirectory</c> — the variant that names its own
    /// retryability. This is the string pinned against the shipped build below.
    /// </summary>
    private const string NotStableText =
        "Current directory at S10.244.4.87:11111:146498551 is not stable to perform the lookup for "
        + "grainId messagehub/Doc (it maps to S10.244.3.44:11111:146515001, which is not a valid silo). "
        + "Retry later.";

    /// <summary>
    /// Verbatim from the production log (#3139) — the REGISTRATION-side variant, thrown by
    /// <c>LocalGrainDirectoryPartition.AddSingleActivation</c> when a stream subscription's
    /// rendezvous grain is handed to a silo membership has already buried. Neither of the two
    /// texts above appears in it, which is exactly why it was never retried.
    /// </summary>
    private const string InvalidSiloRegistrationText =
        "Trying to register pubsubrendezvous/Memory/null/activity/_tracking on invalid silo: "
        + "S10.244.4.125:11111:146517689. Known status: Dead";

    [Theory]
    [InlineData(HopLimitText)]
    [InlineData(NotStableText)]
    [InlineData(InvalidSiloRegistrationText)]
    public void DirectoryInstability_IsTransient(string message)
    {
        // The bare OrleansException the caller actually receives — NOT an
        // OrleansMessageRejectionException. Before this fix nothing in IsTransientFailure matched it,
        // so DeliverToGrainObservable rethrew on the FIRST attempt and the message died there.
        RoutingGrain.IsTransientFailure(new OrleansException(message)).Should().BeTrue(
            "Orleans' grain directory is mid-handoff — a rolling deploy's normal, seconds-long "
            + "window — and Orleans' own message says to ask again. The retry-with-fresh-resolve "
            + "that serves this already exists; the defect was a classifier that could not read "
            + "its input, so the machinery gated on it was unreachable");
    }

    /// <summary>
    /// 🚨 The rule must stay NARROW. <see cref="OrleansException"/> is also Orleans' base for
    /// genuinely terminal conditions (an extension that is not installed, a limit that is exceeded),
    /// and widening the classifier to the TYPE would make every one of those retry six times before
    /// answering — turning a clear defect into a slow one. Only the directory's own retryability
    /// markers qualify.
    /// </summary>
    [Fact]
    public void AnOrdinaryOrleansException_StaysTerminal()
    {
        RoutingGrain.IsTransientFailure(
                new OrleansException("Grain extension not installed on target grain."))
            .Should().BeFalse(
                "widening the match to the OrleansException TYPE would retry real defects — the "
                + "rule is the directory's retryability marker, not the exception's base class");
    }

    /// <summary>
    /// The behavioural half: with the condition classified, the EXISTING retry primitive re-invokes
    /// the grain call — re-resolving the grain each time, which is what lets a directory that has
    /// settled serve the message on a later attempt. Pre-fix this test observes <c>calls == 1</c>
    /// and a faulted task.
    /// </summary>
    [Fact]
    public async Task DirectoryInstability_IsRetriedWithFreshResolve_NotTerminalOnTheFirstAttempt()
    {
        var calls = 0;

        var result = await RoutingGrain.DeliverToGrainObservable(
                grainCall: () =>
                {
                    calls++;
                    return calls <= 2
                        ? Task.FromException<IMessageDelivery>(new OrleansException(HopLimitText))
                        : Task.FromResult<IMessageDelivery>(new MessageDelivery<string>());
                },
                grainKey: "mesh/IJ1R4qkKGkOB0k8Nnn2n0g",
                deliveryId: "dir-instability-1",
                logger: NullLogger.Instance,
                backoff: NoBackoff,
                scheduler: Scheduler.Immediate)
            .Await(TestContext.Current.CancellationToken);

        calls.Should().Be(3,
            "the directory settles within seconds of a membership change, so re-resolving is the "
            + "whole cure — pre-fix the classifier did not match and the call was made exactly once");
        result.State.Should().NotBe(MessageDeliveryState.Failed);
    }

    /// <summary>
    /// 🚨 The second half, and the one that costs a SUBSCRIPTION rather than a message. Both
    /// exception arms in <c>RoutingGrain</c> hard-coded <c>ErrorType.Failed</c> — the same defect
    /// #2346/#2451 removed from the neighbouring result arm and left standing here.
    /// </summary>
    [Fact]
    public void ExhaustedDirectoryInstability_IsNackedAsTransient()
    {
        RoutingGrain.ClassifyDeliveryException(new OrleansException(NotStableText))
            .Should().Be(ErrorType.ShuttingDown,
                "a membership transition is transient BY CONSTRUCTION, and the consumers with their "
                + "own recovery machinery (SynchronizationStream's resubscribe latch, "
                + "MeshNodeStreamCache's transient-owner rule) ride out ShuttingDown and TEAR DOWN "
                + "on Failed — so a terminal verdict here kills mirrors a roll would have restored");
    }

    /// <summary>
    /// 🚨 And it must stay NARROWER than <see cref="RoutingGrain.IsTransientFailure"/>. That
    /// predicate is bounded by a retry budget, so it can afford to be generous; this one arms an
    /// UNBOUNDED resubscribe on the sender's side. A bare timeout — a target silent across the whole
    /// budget, i.e. plausibly wedged rather than restarting — must stay terminal, or the answer is a
    /// resubscribe storm against a hub that never comes back (2026-06-08).
    /// </summary>
    [Fact]
    public void ATimeout_IsRetryableButStillTerminalToTheSender()
    {
        var timeout = new TimeoutException("Response did not arrive on time in 00:00:30");

        RoutingGrain.IsTransientFailure(timeout).Should().BeTrue("retrying a timeout is bounded");
        RoutingGrain.ClassifyDeliveryException(timeout).Should().Be(ErrorType.Failed,
            "telling the sender 'transient' arms an unbounded resubscribe, which is a storm when "
            + "the target is wedged rather than restarting");
    }

    /// <summary>
    /// The host going away is the other genuine lifecycle transition, and it already had this
    /// classification everywhere else (<c>MessageHubGrain.DeliverMessage</c>'s completion arm,
    /// <c>OrleansRoutingService</c>'s shutdown branch, <c>MonolithRoutingService.PostNotFound</c>).
    /// This is the fourth layer agreeing with the other three.
    /// </summary>
    [Fact]
    public void SiloUnavailable_IsNackedAsTransient()
    {
        var siloGone = (Exception)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(SiloUnavailableException));

        RoutingGrain.ClassifyDeliveryException(siloGone).Should().Be(ErrorType.ShuttingDown,
            "the silo hosting the target is leaving — an expected event on every roll, and the "
            + "address answers again moments later on another silo");
    }

    /// <summary>
    /// 🚨 <b>THE ANTI-INERT PIN. If this fact fails after an Orleans upgrade, the classifier above
    /// has gone SILENTLY INERT — repair the marker, never delete the test.</b>
    ///
    /// <para>Orleans gives this condition no exception type of its own, so the rule has to match its
    /// prose (which is this codebase's established contract for exactly this decision — see
    /// <c>OrleansRoutingService.ClassifyRoutedFailure</c>, which names the four layers already doing
    /// it). Prose can be reworded by a dependency bump, and a classifier that stops matching fails
    /// OPEN into the very silence it removes: no error, no log, just replies dying in a roll again.
    /// That is exactly how #2451's <c>GetFailureErrorType</c> sat inert from the day it landed.</para>
    ///
    /// <para>So the marker is asserted against the SHIPPED Orleans assembly's own string literals,
    /// not against a copy of them. Only <see cref="OrleansRoutingService.DirectoryRetryLaterMarker"/>
    /// is pinned this way: the hop-limit wording is quoted from the production log (#2357) and is not
    /// a literal of the currently-pinned build, so it is additive coverage rather than a
    /// build-verified rule — deliberately kept, because the deployed image is not always the version
    /// this repo pins.</para>
    /// </summary>
    [Fact]
    public void RetryLaterMarker_IsStillTheShippedOrleansWording()
    {
        // The phrase is a literal of Orleans.Runtime (LocalGrainDirectory lives there), NOT of
        // Orleans.Core.Abstractions where OrleansException is declared — so the file is located by
        // name beside the test binary rather than off the exception's own assembly.
        var runtimeAssembly = Path.Combine(AppContext.BaseDirectory, "Orleans.Runtime.dll");

        File.Exists(runtimeAssembly).Should().BeTrue(
            $"this fact reads Orleans' own string literals out of {runtimeAssembly}; without the "
            + "file it would pass having verified nothing, which is precisely the failure mode it "
            + "exists to prevent");

        // Managed string literals live in the #US heap as UTF-16 — decoding the whole file and
        // searching is crude, and that is the point: it needs no Orleans-internal API and cannot
        // silently stop looking. The result is reduced to a BOOL before asserting: handing a
        // multi-megabyte subject to a string assertion makes its failure message the whole assembly.
        var literals = Encoding.Unicode.GetString(File.ReadAllBytes(runtimeAssembly));
        // 🚨 OrdinalIgnoreCase, matching the classifier at OrleansRoutingService.cs:590. With
        // Ordinal this pin would go RED for a recasing Orleans could make freely and the classifier
        // would still handle — a false alarm on the one assertion whose entire value is being
        // believed when it fires.
        var present = literals.Contains(
            OrleansRoutingService.DirectoryRetryLaterMarker, StringComparison.OrdinalIgnoreCase);

        present.Should().BeTrue(
            $"'{OrleansRoutingService.DirectoryRetryLaterMarker}' is the phrase "
            + "OrleansRoutingService.IsDirectoryUnstable matches on, and it comes from Orleans' own "
            + "LocalGrainDirectory. If Orleans reworded it, the classifier no longer recognises a "
            + "grain directory mid-handoff — deliveries stop being retried and are NACK'd as "
            + "permanent again (#1742/#2357). REPAIR THE MARKER; do not delete this assertion");
    }

    /// <summary>
    /// 🚨 <b>Issue #3139 — the ATTACH leg, and the half the delivery-leg tests above cannot see.</b>
    ///
    /// <para>Both existing markers are <c>LookupAsync</c>'s — READING a directory entry. Attaching a
    /// hub's cross-process stream subscription WRITES one: <c>SubscribeAsync</c> registers the
    /// stream's <c>PubSubRendezvousGrain</c>, and a registration handed to a silo the membership
    /// oracle has already marked <c>Dead</c> throws a bare <see cref="OrleansException"/> carrying
    /// neither phrase. So <see cref="OrleansRoutingService.IsTransientFailure"/> said "terminal" on
    /// attempt 0, <c>AttachWithBoundedRetry</c>'s budget was never entered, and the hub latched into
    /// "cross-process routing DISABLED" for the rest of its life. 13 occurrences on memex-cloud
    /// across three ReplicaSet generations, 2026-08-23 → 2026-09-02.</para>
    ///
    /// <para>This asserts the CURE, not just the classification: the retry primitive re-invokes the
    /// attach, which is what re-resolves the rendezvous grain against settled membership. Pre-fix
    /// this observes <c>attempts == 1</c> and a faulted observable.</para>
    /// </summary>
    [Fact]
    public async Task AnInvalidSiloRegistration_ReAttachesWithAFreshResolve_RatherThanLatchingDisabled()
    {
        var attempts = 0;

        var handle = await OrleansRoutingService.AttachWithBoundedRetry(
                attach: () =>
                {
                    attempts++;
                    // Two membership-churn rejections, then the directory settles — the shape every
                    // rolling deploy produces, and the one the log shows recurring per churn window.
                    return attempts <= 2
                        ? Task.FromException<string>(new OrleansException(InvalidSiloRegistrationText))
                        : Task.FromResult("attached");
                },
                isTransient: OrleansRoutingService.IsTransientFailure,
                onTransientRetry: (_, _, _) => { },
                backoff: NoBackoff,
                scheduler: Scheduler.Immediate)
            .Await(TestContext.Current.CancellationToken);

        attempts.Should().Be(3,
            "the registration failed against a silo membership had already buried, and a fresh "
            + "SubscribeAsync re-resolves the rendezvous grain — pre-fix the classifier did not "
            + "match this text, so the attach was tried exactly once and the hub's cross-process "
            + "routing was disabled for the rest of its life (#3139)");
        handle.Should().Be("attached");
    }

    /// <summary>
    /// 🚨 <b>THE ANTI-INERT PIN for the registration-side marker. If this fails after an Orleans
    /// upgrade the classifier has gone SILENTLY INERT — repair the marker, never delete the test.</b>
    /// Same contract as <see cref="RetryLaterMarker_IsStillTheShippedOrleansWording"/>, and the
    /// phrase is likewise a literal of the SHIPPED <c>Orleans.Runtime</c> rather than a copy of it.
    /// </summary>
    [Fact]
    public void InvalidSiloMarker_IsStillTheShippedOrleansWording()
    {
        var runtimeAssembly = Path.Combine(AppContext.BaseDirectory, "Orleans.Runtime.dll");

        File.Exists(runtimeAssembly).Should().BeTrue(
            $"this fact reads Orleans' own string literals out of {runtimeAssembly}; without the "
            + "file it would pass having verified nothing");

        ShippedLiteralPresent(runtimeAssembly, OrleansRoutingService.DirectoryInvalidSiloMarker)
            .Should().BeTrue(
                $"'{OrleansRoutingService.DirectoryInvalidSiloMarker}' is the phrase "
                + "IsDirectoryUnstable matches a rendezvous-grain registration against a dead silo "
                + "on, and it comes from Orleans' own LocalGrainDirectoryPartition. If Orleans "
                + "reworded it, a hub's cross-process routing latches DISABLED on the first "
                + "membership-churn window again (#3139). REPAIR THE MARKER; do not delete this");
    }

    /// <summary>
    /// 🚨 <b>Reads the #US heap at BOTH byte alignments, and that is load-bearing.</b> A managed
    /// string literal's UTF-16 payload follows a compressed-integer length, so a blob can begin at
    /// an ODD file offset — decoding the file from offset 0 only, as this pin originally did, then
    /// misses a phrase that is genuinely present. That is a FALSE RED on the one assertion whose
    /// entire value is being believed when it fires: it reports "Orleans reworded the message" for
    /// a heap layout that shifted. Measured 2026-09-02 — searching from offset 0 alone reports
    /// <c>on invalid silo</c> ABSENT from the pinned 10.2.2 build, where it is in fact present.
    /// </summary>
    private static bool ShippedLiteralPresent(string assemblyPath, string phrase)
    {
        var raw = File.ReadAllBytes(assemblyPath);

        // OrdinalIgnoreCase on both decodes, matching the classifier — a recasing Orleans could
        // make freely is one the classifier still handles, so it must not red this pin.
        return Encoding.Unicode.GetString(raw)
                   .Contains(phrase, StringComparison.OrdinalIgnoreCase)
            || Encoding.Unicode.GetString(raw, 1, raw.Length - 1)
                   .Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }
}
