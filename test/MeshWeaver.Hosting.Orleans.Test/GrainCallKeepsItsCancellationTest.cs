using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issue #5731, the DIAGNOSTICS half only: a grain call Orleans CANCELS keeps Orleans' own sentence
/// on its way to the log, the trace and the sender's NACK.
///
/// <para>Orleans cancels a message that expired while its target grain was still being placed with
/// an <see cref="OperationCanceledException"/> the caller receives as a CANCELLED task. Rx's task
/// bridge reported that task as a fresh <see cref="TaskCanceledException"/> ("A task was
/// canceled."), so production's NACK named nothing. <c>RoutingGrain.ObserveGrainCall</c> hands over
/// the exception the task stored instead.</para>
///
/// <para><b>What this deliberately does NOT change, and pins.</b> The verdict: a placement expiry
/// is still reported to the sender as <see cref="ErrorType.Failed"/> and is still not re-sent. Why
/// placement stalled is not established, so nothing here calls it transient.</para>
///
/// <para>The facts are pure and need no cluster; the exception text is quoted verbatim from the
/// production log.</para>
/// </summary>
public class GrainCallKeepsItsCancellationTest
{
    private static readonly Func<int, TimeSpan> NoBackoff = _ => TimeSpan.Zero;

    private const string GrainKey = "messagehub/Signature/Desk/_Activity/compile-1";
    private const string AddressPath = "Signature/Desk/_Activity/compile-1";

    /// <summary>What Rx's task bridge says about ANY cancelled task.</summary>
    private const string RxStandInText = "A task was canceled.";

    /// <summary>
    /// Verbatim from the production log (#5731, 2026-09-24 13:43:20Z, Orleans.Messaging[100071]).
    /// </summary>
    private const string PlacementExpiredText =
        "Message expired before placement could complete for grain "
        + "messagehub/Signature/Desk/_Activity/compile-20260924134235046d3415968dc0b4b9cad1eddb1be5b8bb6.";

    /// <summary>
    /// The SHAPE Orleans hands the caller: an async completion that throws an
    /// <see cref="OperationCanceledException"/> ends as a CANCELLED task that still stores it.
    /// </summary>
    private static async Task<IMessageDelivery> CancelledWith(Exception cancellation)
    {
        await Task.Yield();
        throw cancellation;
    }

    private static async Task<IMessageDelivery> FaultedWith(Exception fault)
    {
        await Task.Yield();
        throw fault;
    }

    private static Task<Notification<T>> Terminal<T>(IObservable<T> source) =>
        source.Materialize().Take(1).Await(TestContext.Current.CancellationToken);

    private static Task<List<(string Message, ErrorType Type)>> NacksOf(
        Func<Task<IMessageDelivery>> grainCall, string deliveryId) =>
        Observable.Defer(() =>
            {
                var nacks = new List<(string Message, ErrorType Type)>();
                return RoutingGrain.DeliverToGrainRoute(
                        grainCall: grainCall,
                        grainKey: GrainKey,
                        addressPath: AddressPath,
                        deliveryId: deliveryId,
                        postFailureToSender: (message, type) => nacks.Add((message, type)),
                        logger: NullLogger.Instance,
                        backoff: NoBackoff,
                        scheduler: Scheduler.Immediate)
                    .Select(_ => nacks);
            })
            .Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// THE FIX. Fails without it: the NACK then reads "Delivery to '…' failed: A task was canceled."
    /// </summary>
    [Fact]
    public async Task APlacementExpiry_IsNackedWithOrleansOwnSentence()
    {
        var nacks = await NacksOf(
            () => CancelledWith(new OperationCanceledException(PlacementExpiredText)), "keeps-sentence-1");

        nacks.Count.Should().Be(1, "the route answers the sender exactly once");
        nacks[0].Message.Contains(PlacementExpiredText, StringComparison.Ordinal).Should().BeTrue(
            $"the sender is told what Orleans said; got '{nacks[0].Message}'");
        nacks[0].Message.Contains(RxStandInText, StringComparison.Ordinal).Should().BeFalse(
            $"Rx's stand-in text names nothing; got '{nacks[0].Message}'");
    }

    /// <summary>
    /// THE NEGATIVE CONTROL ON THE VERDICT. Keeping the sentence must not change what the sender is
    /// told to DO: a placement expiry stays terminal and is sent once.
    /// </summary>
    [Fact]
    public async Task APlacementExpiry_IsStillTerminal_AndStillNotResent()
    {
        var calls = 0;

        var nacks = await NacksOf(
            () =>
            {
                Interlocked.Increment(ref calls);
                return CancelledWith(new OperationCanceledException(PlacementExpiredText));
            },
            "keeps-sentence-2");

        nacks.Count.Should().Be(1);
        nacks[0].Type.Should().Be(ErrorType.Failed,
            "why placement stalled is not established, so the verdict is unchanged: a consumer told "
            + "ShuttingDown resubscribes without bound");
        calls.Should().Be(1, "a cancellation is not a re-sendable failure, before or after this change");

        var expired = new OperationCanceledException(PlacementExpiredText);
        RoutingGrain.ClassifyDeliveryException(expired).Should().Be(ErrorType.Failed);
        RoutingGrain.ClassifyPodHubDeliveryException(expired).Should().Be(ErrorType.Failed,
            "the pod-hub leg rides the same bridge and classifies through the same arms");
        RoutingGrain.IsResendableDeliveryFailure(expired).Should().BeFalse();
        RoutingGrain.IsTransientFailure(expired).Should().BeFalse();
    }

    /// <summary>
    /// The mechanism, both sides in one fact: the bridge this replaced loses the sentence, the
    /// route's own bridge keeps it. If the first half ever stops holding (Rx starts handing over the
    /// stored exception itself), <c>ObserveGrainCall</c>'s catch has become dead code.
    /// </summary>
    [Fact]
    public async Task RxsTaskBridgeLosesTheSentence_AndTheGrainCallBridgeKeepsIt()
    {
        Func<Task<IMessageDelivery>> call =
            () => CancelledWith(new OperationCanceledException(PlacementExpiredText));

        var viaRx = await Terminal(Observable.Defer(() => call().ToObservable()));

        viaRx.Kind.Should().Be(NotificationKind.OnError);
        (viaRx.Exception is TaskCanceledException).Should().BeTrue(
            $"Rx reports a cancelled task as its own stand-in; got {viaRx.Exception}");
        viaRx.Exception!.Message.Contains(PlacementExpiredText, StringComparison.Ordinal).Should().BeFalse(
            "this is the loss the change repairs");

        var viaGrainCall = await Terminal(RoutingGrain.ObserveGrainCall(call));

        viaGrainCall.Kind.Should().Be(NotificationKind.OnError);
        viaGrainCall.Exception!.GetType().Should().Be(typeof(OperationCanceledException),
            "the exception the task stored is handed over as it is");
        viaGrainCall.Exception.Message.Should().Be(PlacementExpiredText);
    }

    [Fact]
    public async Task TheRetryLeg_SurfacesTheStoredCancellation()
    {
        var terminal = await Terminal(RoutingGrain.DeliverToGrainObservable(
            grainCall: () => CancelledWith(new OperationCanceledException(PlacementExpiredText)),
            grainKey: GrainKey,
            deliveryId: "keeps-sentence-3",
            logger: NullLogger.Instance,
            backoff: NoBackoff,
            scheduler: Scheduler.Immediate));

        terminal.Kind.Should().Be(NotificationKind.OnError);
        terminal.Exception!.Message.Should().Be(PlacementExpiredText);
    }

    [Fact]
    public async Task AnyOtherCancellation_KeepsItsOwnText_AndStaysTerminal()
    {
        var nacks = await NacksOf(
            () => CancelledWith(new OperationCanceledException("The hub's own cancellation, in its own words.")),
            "keeps-sentence-4");

        nacks.Count.Should().Be(1);
        nacks[0].Type.Should().Be(ErrorType.Failed);
        nacks[0].Message.Contains("The hub's own cancellation, in its own words.", StringComparison.Ordinal)
            .Should().BeTrue($"got '{nacks[0].Message}'");
    }

    /// <summary>
    /// A task cancelled by a TOKEN stores no exception of its own; the bridge must then say what it
    /// said before rather than fail or invent text.
    /// </summary>
    [Fact]
    public async Task ACancellationThatStoredNothing_IsReportedAsBefore()
    {
        var nacks = await NacksOf(
            () => Task.FromCanceled<IMessageDelivery>(new CancellationToken(canceled: true)),
            "keeps-sentence-5");

        nacks.Count.Should().Be(1);
        nacks[0].Type.Should().Be(ErrorType.Failed);
        nacks[0].Message.Contains(RxStandInText, StringComparison.Ordinal).Should().BeTrue(
            $"there is no better sentence to report; got '{nacks[0].Message}'");

        var withoutATask = new TaskCanceledException("no task behind this one");
        ReferenceEquals(RoutingGrain.OriginalCancellation(withoutATask), withoutATask).Should().BeTrue(
            "with no cancelled task to read, the exception is handed back untouched");
    }

    [Fact]
    public async Task AFault_AndAResult_PassThroughUntouched()
    {
        var fault = new InvalidOperationException("a genuine defect");

        var faulted = await Terminal(RoutingGrain.ObserveGrainCall(() => FaultedWith(fault)));

        faulted.Kind.Should().Be(NotificationKind.OnError);
        ReferenceEquals(faulted.Exception, fault).Should().BeTrue("only a cancelled task is re-read");

        var answered = await Terminal(RoutingGrain.ObserveGrainCall(() => Task.FromResult(42)));

        answered.Kind.Should().Be(NotificationKind.OnNext);
        answered.Value.Should().Be(42);
    }

    [Fact]
    public async Task TheGrainCallStaysCold_OneInvocationPerSubscription()
    {
        var calls = 0;
        var cold = RoutingGrain.ObserveGrainCall(() => Task.FromResult(Interlocked.Increment(ref calls)));

        calls.Should().Be(0, "building the observable invokes nothing");
        (await Terminal(cold)).Value.Should().Be(1);
        (await Terminal(cold)).Value.Should().Be(2,
            "the retry around it re-invokes the grain call from scratch on every re-subscribe");
    }

    /// <summary>
    /// WHY THE VERDICT CANNOT MOVE, as a fact rather than an argument. Only a CANCELLED task is
    /// re-read, and what it stores is an <see cref="OperationCanceledException"/>. Every type the
    /// delivery classifier turns into <see cref="ErrorType.ShuttingDown"/> is NOT one - those arrive
    /// as FAULTED tasks, which this change does not touch. So what is handed over differs from Rx's
    /// stand-in in its text, never in whether one of these arms matches it.
    ///
    /// <para>If Orleans ever makes one of these a cancellation, this fact goes red and "diagnostics
    /// only" has to be re-examined rather than assumed. (A stored cancellation that WRAPS one of
    /// them as an inner exception would also be seen now; no such shape is known.)</para>
    /// </summary>
    [Theory]
    [InlineData(typeof(OrleansException))]
    [InlineData(typeof(OrleansMessageRejectionException))]
    [InlineData(typeof(SiloUnavailableException))]
    [InlineData(typeof(OrleansLifecycleCanceledException))]
    [InlineData(typeof(global::Orleans.Runtime.Messaging.ConnectionFailedException))]
    [InlineData(typeof(ObjectDisposedException))]
    public void NoTypeTheClassifierCallsTransient_IsACancellation(Type classified)
    {
        typeof(OperationCanceledException).IsAssignableFrom(classified).Should().BeFalse(
            $"{classified.Name} would then reach the delivery classifier through a cancelled task, "
            + "where Rx's stand-in used to hide it - a verdict change this fix does not intend");
    }
}
