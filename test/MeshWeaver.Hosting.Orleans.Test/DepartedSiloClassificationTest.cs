using System;
using System.IO;
using System.Text;
using MeshWeaver.Messaging;
using Orleans.Runtime;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issues #2299 and #2307 — the departed-silo half of the delivery classifier, and the two
/// issues are ONE ROOT seen through two different logs.</b>
///
/// <para>#2299 is fingerprinted on MeshWeaver's OWN line (<c>[ROUTE] Directed delivery to pod hub …
/// failed — surfacing <b>Failed</b> DeliveryFailure to sender …</c>) — the VERDICT. #2307 is
/// fingerprinted on Orleans' own <c>Orleans.Messaging[100071] Failed to address message …</c> — one
/// line per addressing ATTEMPT, which <c>DeliverToGrainObservable</c>'s six retries multiply. Same
/// condition, same rejection texts, opposite ends of the same call.</para>
///
/// <para><b>What was left after #2314 fixed the retry half.</b>
/// <c>RoutingGrain.ClassifyDeliveryException</c> answers <see cref="ErrorType.ShuttingDown"/> only
/// for the grain directory mid-handoff, the host going away and the container being disposed — and
/// <c>IsShutdownShaped</c> recognised the host going away by TWO TYPE TESTS only
/// (<see cref="SiloUnavailableException"/>, <see cref="OrleansLifecycleCanceledException"/>).
/// Production's departed-silo rejections carry NEITHER type, so after the six re-resolving retries
/// the sender still received a terminal <see cref="ErrorType.Failed"/>, and the consumers that carry
/// their own recovery machinery (<c>SynchronizationStream</c>'s resubscribe latch,
/// <c>MeshNodeStreamCache</c>'s transient-owner rule) ride out <see cref="ErrorType.ShuttingDown"/>
/// and TEAR DOWN on <see cref="ErrorType.Failed"/> — so an ordinary roll permanently killed mirrors
/// that would have resumed seconds later on the surviving pod.</para>
///
/// <para>These facts are pure and need no cluster — the classifier is <c>internal static</c> and
/// every exception text below is quoted VERBATIM from the production log rather than paraphrased, so
/// a reader can see exactly what is being matched.</para>
/// </summary>
public class DepartedSiloClassificationTest
{
    /// <summary>
    /// Verbatim from <c>Admin/_LogIncident/e849e4a7795e0c92</c> (#2299 body, 2026-08-23 19:51:58Z) —
    /// the pending-retry form, in which Orleans' own transport states that it considers the condition
    /// retryable ("will retry after 585.8766ms") while the router surfaced a terminal failure anyway.
    /// </summary>
    private const string ConnectPendingRetryText =
        "Exception while sending message: Orleans.Runtime.Messaging.ConnectionFailedException: "
        + "Unable to connect to S10.244.4.87:11111:146498551, will retry after 585.8766ms";

    /// <summary>
    /// Verbatim from #2299's 2026-09-17 bot fold (sample of 2026-09-02 19:26:22Z) — the socket-level
    /// form, where the pod's address is not routable at all because the pod is gone.
    /// </summary>
    private const string HostUnreachableText =
        "Exception while sending message: Orleans.Runtime.Messaging.ConnectionFailedException: "
        + "Unable to connect to endpoint S10.244.3.122:11111:147265510. See InnerException"
        + " ---> Orleans.Networking.Shared.SocketConnectionException: "
        + "Unable to connect to 10.244.3.122:11111. Error: HostUnreachable";

    /// <summary>
    /// Verbatim from #2307's newest bot fold (2026-09-19T13:26Z, sample of 2026-09-19 09:03:41Z) —
    /// the same shape with nothing listening on the port rather than no route to the host. This is
    /// the form every one of that incident's newest 21 occurrences carries.
    /// </summary>
    private const string ConnectionRefusedText =
        "Exception while sending message: Orleans.Runtime.Messaging.ConnectionFailedException: "
        + "Unable to connect to endpoint S10.244.2.223:11111:148812047. See InnerException"
        + " ---> Orleans.Networking.Shared.SocketConnectionException: "
        + "Unable to connect to 10.244.2.223:11111. Error: ConnectionRefused";

    /// <summary>
    /// Verbatim from #2307's body — the superseded-generation form, named there as the second of that
    /// incident's two distinct inner failures. The SAME pod address with a NEW generation: a silo
    /// restarted and reclaimed its endpoint while callers still held the old incarnation.
    /// </summary>
    private const string SupersededGenerationText =
        "The target silo is no longer active: target was S10.244.4.183:11111:146524552, but this "
        + "silo is S10.244.4.183:11111:146534005. The rejected message is Request "
        + "[S10.244.3.168:11111:146524531 sys.client/hosted-10.244.3.168:11111@146524531]->"
        + "[S10.244.4.183:11111:146524552 sys.svc.dir.mem/10.244.4.183:11111@146524552] "
        + "Orleans.GrainDirectory.IDhtGrainDirectory.LookupAsync(Orleans.Runtime.GrainId, "
        + "System.Int32) #6B5E67B0E5D7AFC9.";

    /// <summary>
    /// The four production shapes, all of them statements about ONE silo INCARNATION — a
    /// <c>SiloAddress</c> is generation-stamped, so "cannot connect to
    /// S10.244.4.87:11111:<b>146498551</b>" and "target was …:<b>146524552</b>, but this silo is
    /// …:<b>146534005</b>" both say the addressed incarnation will never answer again. That is the
    /// bar this classifier sets: a lifecycle transition BY CONSTRUCTION, the same bar the grain
    /// directory mid-handoff and the host going away already clear.
    ///
    /// <para>Pre-fix every one of these returns <see cref="ErrorType.Failed"/>.</para>
    /// </summary>
    [Theory]
    [InlineData(ConnectPendingRetryText)]
    [InlineData(HostUnreachableText)]
    [InlineData(ConnectionRefusedText)]
    [InlineData(SupersededGenerationText)]
    public void ADepartedSilo_IsNackedAsTransient(string rejectionText)
    {
        RoutingGrain.ClassifyDeliveryException(new OrleansException(rejectionText))
            .Should().Be(ErrorType.ShuttingDown,
                "the silo incarnation the message was addressed to is gone — every roll produces "
                + "this window and the target answers again on another pod moments later. Told "
                + "terminally, the consumers that carry their own recovery machinery tear down "
                + "instead of riding it out (#2299 / #2307)");
    }

    /// <summary>
    /// 🚨 <b>The type guard's PREMISE, pinned rather than assumed.</b> The predicate tests
    /// <c>e is OrleansException</c>, and production delivers these as
    /// <see cref="OrleansMessageRejectionException"/>. If Orleans ever re-parented that type the
    /// guard would stop matching and the fix would go silently inert — the same failure mode the
    /// marker pins below exist for.
    /// </summary>
    [Fact]
    public void TheProductionRejection_IsAnOrleansException()
    {
        typeof(OrleansMessageRejectionException).IsSubclassOf(typeof(OrleansException))
            .Should().BeTrue(
                "every sample in both incidents names Orleans.Runtime.OrleansMessageRejectionException "
                + "as the exception type, and RoutingGrain.IsDepartedSiloRejection narrows on the "
                + "OrleansException base it derives from");
    }

    /// <summary>
    /// 🚨 <b>The rejection TYPE is deliberately not enough</b> — the mutation that matters. An
    /// <see cref="OrleansMessageRejectionException"/> carries EVERY kind of refusal, including
    /// genuinely terminal ones, so accepting the type alone would report real defects as transient
    /// and arm a resubscribe against them. Dropping the phrase test from the predicate turns this
    /// fact red, which is what shows the phrase is load-bearing rather than decoration.
    /// </summary>
    [Fact]
    public void ARejectionWithoutADepartedSiloPhrase_StaysTerminal()
    {
        var rejection = (Exception)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(OrleansMessageRejectionException));

        RoutingGrain.ClassifyDeliveryException(rejection).Should().Be(ErrorType.Failed,
            "the rejection type says nothing about WHY Orleans refused — only the phrase does, and "
            + "'anything this does not recognise stays terminal' is what keeps a genuine defect "
            + "reported as one");
    }

    /// <summary>
    /// 🚨 <b>The OTHER mutation: dropping the <c>is OrleansException</c> guard.</b> "Unable to
    /// connect to" is a phrase an application-level connect failure can carry too, and one of those
    /// is a genuine defect. The guard is what makes the phrase a statement about a CLUSTER endpoint:
    /// <c>ConnectionManager</c> addresses silos and gateways, nothing else.
    /// </summary>
    [Fact]
    public void AnApplicationConnectFailureCarryingTheSamePhrase_StaysTerminal()
    {
        var notACluster = new InvalidOperationException(
            "Unable to connect to https://example.invalid — the configured endpoint refused");

        RoutingGrain.ClassifyDeliveryException(notACluster).Should().Be(ErrorType.Failed,
            "without the OrleansException guard the phrase would demote ordinary connect defects "
            + "anywhere in the delivery path, which is the one direction this classifier must not "
            + "fail in");
    }

    /// <summary>
    /// 🚨 <b>An ordinary Orleans fault must still be terminal</b> — the same narrowness
    /// <c>OrleansDirectoryInstabilityClassificationTest.AnOrdinaryOrleansException_StaysTerminal</c>
    /// pins for the directory markers. <see cref="OrleansException"/> is also Orleans' base for
    /// genuinely permanent conditions.
    /// </summary>
    [Fact]
    public void AnOrdinaryOrleansFault_StaysTerminal()
    {
        RoutingGrain.ClassifyDeliveryException(
                new OrleansException("Grain extension not installed on target grain."))
            .Should().Be(ErrorType.Failed,
                "the rule is the departed-silo phrase, not the exception's base class");
    }

    /// <summary>
    /// 🚨 <b>Found through the exception GRAPH, not the <c>InnerException</c> line — and this half
    /// was narrow for the two pre-existing TYPE tests as well.</b>
    /// <c>AggregateException.InnerException</c> yields <c>InnerExceptions[0]</c> ONLY, so the old
    /// line-walk missed a rejection at any other index — and <c>PostFailure</c>'s two-transport
    /// aggregate plus the Rx <c>Catch</c> arms make that index a RACE, i.e. the classification
    /// depended on which fault happened to arrive first. Widening the walk to
    /// <c>ExceptionChain.Contains</c> fixes the silo-departed shapes and the two type tests at once.
    /// </summary>
    [Fact]
    public void ItIsFoundBesideAnotherFaultInAnAggregate_NotOnlyAtIndexZero()
    {
        var twoTransports = new AggregateException(
            new InvalidOperationException("the NACK's other transport also failed"),
            new OrleansException(ConnectionRefusedText));

        RoutingGrain.ClassifyDeliveryException(twoTransports).Should().Be(ErrorType.ShuttingDown,
            "which fault sits at index 0 of PostFailure's two-transport aggregate is a race, so a "
            + "classification that reads index 0 only answers differently run to run");
    }

    /// <summary>
    /// The pre-existing type tests keep their answer through the widened walk — this is the
    /// regression guard on the rewrite, not on the new rule.
    /// </summary>
    [Fact]
    public void SiloUnavailable_IsStillNackedAsTransient()
    {
        var siloGone = (Exception)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(SiloUnavailableException));

        RoutingGrain.ClassifyDeliveryException(siloGone).Should().Be(ErrorType.ShuttingDown,
            "IsShutdownShaped's two original type tests must survive the change from an "
            + "InnerException line-walk to an ExceptionChain graph walk");
    }

    /// <summary>
    /// 🚨 <b>THE ANTI-INERT PIN for the unreachable-endpoint marker. If this fact fails after an
    /// Orleans upgrade the classifier above has gone SILENTLY INERT — repair the marker, never
    /// delete the test.</b>
    ///
    /// <para>Orleans gives this condition no exception type this assembly can name, so the rule
    /// matches its prose — this codebase's established contract for exactly this decision
    /// (<c>OrleansRoutingService.ClassifyRoutedFailure</c> names the four layers already doing it).
    /// Prose can be reworded by a dependency bump, and a classifier that stops matching fails OPEN
    /// into the very silence it removes: no error, no log, just mirrors dying in a roll again.</para>
    ///
    /// <para>The phrase is a literal of <b>Orleans.Core</b>, where <c>ConnectionManager</c> lives —
    /// NOT of Orleans.Runtime, which is where every other marker in this codebase comes from.</para>
    /// </summary>
    [Fact]
    public void TheUnreachableEndpointMarker_IsStillTheShippedOrleansWording()
    {
        ShippedLiteralPresent("Orleans.Core.dll", RoutingGrain.SiloEndpointUnreachableMarker)
            .Should().BeTrue(
                $"'{RoutingGrain.SiloEndpointUnreachableMarker}' is the phrase "
                + "RoutingGrain.IsDepartedSiloRejection matches an unreachable silo endpoint on, and "
                + "it comes from Orleans' own ConnectionManager. If Orleans reworded it, a delivery "
                + "to a departed pod is NACK'd as permanent again and every live mirror on that path "
                + "is torn down by a roll (#2299 / #2307). REPAIR THE MARKER; do not delete this");
    }

    /// <summary>
    /// 🚨 <b>THE ANTI-INERT PIN for the superseded-generation marker.</b> Same contract as
    /// <see cref="TheUnreachableEndpointMarker_IsStillTheShippedOrleansWording"/>; this phrase is a
    /// literal of <b>Orleans.Runtime</b>, where the target-silo check lives.
    /// </summary>
    [Fact]
    public void TheSupersededGenerationMarker_IsStillTheShippedOrleansWording()
    {
        ShippedLiteralPresent("Orleans.Runtime.dll", RoutingGrain.SupersededSiloGenerationMarker)
            .Should().BeTrue(
                $"'{RoutingGrain.SupersededSiloGenerationMarker}' is the phrase "
                + "RoutingGrain.IsDepartedSiloRejection matches a silo that restarted and reclaimed "
                + "its address on. If Orleans reworded it, that shape is reported as permanent "
                + "again. REPAIR THE MARKER; do not delete this");
    }

    /// <summary>
    /// 🚨 <b>Reads the #US heap at BOTH byte alignments, and that is load-bearing</b> — the same
    /// mechanism <c>OrleansDirectoryInstabilityClassificationTest.ShippedLiteralPresent</c>
    /// documents. A managed string literal's UTF-16 payload follows a compressed-integer length, so
    /// a blob can begin at an ODD file offset; decoding from offset 0 only then misses a phrase that
    /// is genuinely present — a FALSE RED on the one assertion whose entire value is being believed
    /// when it fires.
    ///
    /// <para>The assembly is located by NAME beside the test binary because the two phrases live in
    /// two different Orleans assemblies, neither of which is the one declaring
    /// <see cref="OrleansException"/>.</para>
    /// </summary>
    private static bool ShippedLiteralPresent(string assemblyFileName, string phrase)
    {
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyFileName);

        File.Exists(assemblyPath).Should().BeTrue(
            $"this fact reads Orleans' own string literals out of {assemblyPath}; without the file "
            + "it would pass having verified nothing, which is precisely the failure mode it exists "
            + "to prevent");

        var raw = File.ReadAllBytes(assemblyPath);

        // OrdinalIgnoreCase on both decodes, matching the classifier — a recasing Orleans could make
        // freely is one the classifier still handles, so it must not red this pin.
        return Encoding.Unicode.GetString(raw)
                   .Contains(phrase, StringComparison.OrdinalIgnoreCase)
            || Encoding.Unicode.GetString(raw, 1, raw.Length - 1)
                   .Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }
}
