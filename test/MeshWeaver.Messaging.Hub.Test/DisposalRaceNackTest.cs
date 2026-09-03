using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The FOURTH door into the silent-abandonment park (#672 closed two, #1029 a third): a delivery
/// the hub ACCEPTED — it was already in the main turn queue, never deferred — that only reaches
/// the handler seam once disposal has advanced. Both outcomes at that seam used to answer nobody,
/// so the sender's <c>hub.Observe(...)</c> burned its entire 60 s request budget in silence.
///
/// <para><b>Why the earlier fixes do not cover it.</b> <c>ScheduleNotify</c>'s intake gate NACKs a
/// message that ARRIVES after <c>RunLevel</c> flipped, and the disposal drain NACKs whatever is
/// still parked in <c>deferredDeliveries</c>. This delivery is neither: it was accepted while the
/// hub was healthy and it is in the MAIN queue, so the intake gate never saw it and the deferred
/// drain finds nothing to answer for it.</para>
///
/// <para><b>Field evidence.</b> <c>StaleStampRootBindingTest</c> on CI (run 31390882509, the merge
/// that turned main red): the installer's <c>SyncContentFilesRequest</c> activated the package
/// root's hub, the root was recycled ~94 ms later, and the request executed at
/// <c>runLevel=Dead</c> — <c>NOT_PROCESSED_DISPOSING</c>, no NACK. The installer waited the full
/// 60 s hub timeout and then reported "the package's nodes are installed but its binaries are not
/// being served". The same test locally shows the other half: a client <c>SubscribeRequest</c>
/// reaching <c>LayoutAreaHost</c> in the recycle window faults with <c>HubDisposingException</c>,
/// and the sender still hears nothing.</para>
///
/// <para><b>The contract pinned here.</b> Both outcomes must NACK through the PARENT hub with the
/// TRANSIENT <see cref="ErrorType.ShuttingDown"/> — through the parent because the disposing hub's
/// own <c>Post</c> re-enters <c>ReportFailure</c>'s "don't post during shutdown" gate
/// (<c>RunLevel &gt;= DisposeHostedHubs</c>) and is dropped, which is precisely how the disposal
/// branch's NACK went missing while looking correct in the source.</para>
/// </summary>
public class DisposalRaceNackTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record RaceRequest : IRequest<RaceResponse>;

    private record RaceResponse;

    private record FaultingRequest : IRequest<RaceResponse>;

    private record Blocker;

    private static readonly Address VictimAddress = new("disposal-race", "1");

    private static readonly Address FaultingAddress = new("disposal-race-fault", "1");

    /// <summary>
    /// Door one: the delivery reaches <c>HandleMessage</c> with the hub already past
    /// <c>ShutDown</c>, so no handler is entered at all (<c>NOT_PROCESSED_DISPOSING</c>). It used
    /// to be returned unchanged — abandoned without a word to the sender.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AcceptedRequest_IsNacked_WhenTheHubGoesDownBeforeItsTurnRuns()
        => await RunRace(VictimAddress, faulting: false);

    /// <summary>
    /// Door two: the delivery DOES reach its handler, which cannot complete because the hub is
    /// tearing down (<see cref="HubDisposingException"/> — the production shape is
    /// <c>LayoutAreaHost</c> failing to build its <c>SynchronizationStream</c>). The branch that
    /// classifies this as transient posted its NACK through <c>ReportFailure</c>, whose own
    /// shutdown gate then swallowed it — the classification was right and the answer never left
    /// the hub.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FaultingRequest_IsNacked_WhenTheHandlerRacesDisposal()
        => await RunRace(FaultingAddress, faulting: true);

    private async Task RunRace(Address victimAddress, bool faulting)
    {
        // 🚨 No hand-woven gate: handler → test is an AsyncSubject the producer completes; the
        // release travels INTO the deliberately parked action block, so it is a volatile flag
        // polled under a bounded SpinUntil and written in the `finally` below.
        var handlerEntered = new AsyncSubject<Unit>();
        var releaseHandler = 0;

        var host = GetHost();
        var victim = host.GetHostedHub(victimAddress, c => c
            .WithTypes(typeof(RaceRequest), typeof(FaultingRequest), typeof(RaceResponse), typeof(Blocker))
            // Deliberately ignores cancellation: keeps the victim's single-threaded action block
            // busy so the ShutdownRequest posted by Dispose() cannot be processed. Disposal then
            // completes out of band (the disposal watchdog), which is what lets RunLevel advance
            // while our request is still sitting in the main queue — the production interleaving,
            // made deterministic.
            .WithHandler<Blocker>((_, d) =>
            {
                handlerEntered.OnNext(Unit.Default);
                handlerEntered.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref releaseHandler) == 1, TimeSpan.FromSeconds(60));
                return d.Processed();
            })
            // Both handlers WOULD answer / would be entered, so a pass can only come from the
            // NACK — never from the request quietly being served after all.
            .WithHandler<RaceRequest>((h, d) =>
            {
                h.Post(new RaceResponse(), o => o.ResponseFor(d));
                return d.Processed();
            })
            .WithHandler<FaultingRequest>((h, _) =>
                throw new HubDisposingException(h.Address, "area/Overview")));
        victim.Should().NotBeNull();

        try
        {
            // 1. Stall the victim's turn loop.
            host.Post(new Blocker(), o => o.WithTarget(victimAddress));
            await handlerEntered.Should().Within(20.Seconds()).Emit(
                "the blocker handler must be holding the victim's action block");

            // 2. Accepted while the hub is healthy, so it lands in the MAIN queue behind the
            //    stall — not in the deferred queue the #672 drain answers for.
            IRequest<RaceResponse> request = faulting ? new FaultingRequest() : new RaceRequest();
            var response = host
                .Observe<RaceResponse>(request, o => o.WithTarget(victimAddress))
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);

            // 3. Dispose. The phase machine is starved behind the stall, so the disposal watchdog
            //    tears the hub down out of band and RunLevel reaches Dead with our request still
            //    queued. Polls the public RunLevel rather than sleeping a fixed budget.
            victim!.Dispose();
            await WaitUntil(() => victim.RunLevel >= MessageHubRunLevel.ShutDown,
                "the victim must reach ShutDown while the request is still queued");
            Output.WriteLine($"victim RunLevel at release: {victim.RunLevel}");

            // 4. Let the turn loop reach our request.
            Volatile.Write(ref releaseHandler, 1);

            // WITHOUT the fix this task never completes and the [Fact] timeout fires with no
            // explanation — exactly the production symptom (an install that spins for 60 s, a
            // page that never renders).
            var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
            failure.Failure.Should().NotBeNull();
            Output.WriteLine(
                $"NACK: errorType={failure.Failure!.ErrorType} message={failure.Failure.Message}");

            // TRANSIENT: a recycled address reactivates, so the sender must read "ask again",
            // never "gone". SynchronizationStream's resubscribe latch keys off exactly this.
            failure.Failure.ErrorType.Should().Be(ErrorType.ShuttingDown,
                "the address may reactivate (recycle / restart), so a terminal classification "
                + "would kill the consumer's rehydrate path");
            failure.Failure.Message.Should().Contain("shutting down",
                "the transient classifiers (MeshNodeStreamCache.IsTransientOwnerFailure, "
                + "AreaErrorClassifier.IsTransientHubFailure) match this phrase when the typed "
                + "failure has been flattened into a DeliveryFailureException message");

            // 🚨 #3017 — and it must be recognisable as THIS OWNER's answer, not merely as some
            // transient failure. A caller discriminating "the owner refused me" from "the routing
            // layer could not reach it" reads ShutdownNack.IsAnsweredByOwner; the two live seams
            // that mint this NACK (the late turn, and the handler that threw
            // HubDisposingException) are pinned here against the REAL message rather than against
            // a copy of its wording, because enumerating the sentences is exactly what failed.
            ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, victimAddress).Should().BeTrue(
                "the owner at " + victimAddress + " refused this delivery, so its own banner must "
                + "be in the message — a caller that cannot tell this from a routing failure "
                + "cannot tell 'ask again at the fresh activation' from 'there is nothing there'");
        }
        finally
        {
            Volatile.Write(ref releaseHandler, 1);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting: {because}");
    }
}
