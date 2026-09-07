using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Deterministic repro for the RECYCLE-WINDOW SUBSCRIBE crash: a
/// <see cref="SubscribeRequest"/> for a layout area that lands on a hub which has just begun
/// disposing used to fail with a <b>NullReferenceException</b> inside
/// <c>LayoutAreaHost</c>'s constructor, and that reached the subscriber as a TERMINAL
/// <see cref="DeliveryFailureException"/>.
///
/// <para><b>The window is structural, and #3506 made it SMALLER without closing it.</b>
/// <c>MessageHub.Dispose</c> freezes hosted-hub creation SYNCHRONOUSLY, on its very first
/// statement (<c>HostedHubsCollection.CloseCreation</c>, which cascades through the whole
/// subtree), and only THEN posts the <c>ShutdownRequest</c> that moves <c>RunLevel</c> off
/// <c>Started</c>. Serving a layout area means creating a hosted sub-hub for its
/// <c>SynchronizationStream</c>, so between those two moments the hub accepts work it can no
/// longer perform. The stream constructor used to paper over the refusal by fabricating a "dead
/// stream" with <c>Hub = null!</c>; <c>LayoutAreaHost</c> dereferenced
/// <c>Stream.Hub.ServiceProvider</c> on the next line.</para>
///
/// <para>Message intake used to stay open for the WHOLE <c>Quiescing</c> drain, so a subscribe
/// that merely ARRIVED during teardown also reached the layout stack. MeshWeaver#3506 closed the
/// intake gate at the first instant of disposal, which removes that arrival path: such a request
/// is now turned away at the door with the same transient <c>ShuttingDown</c> NACK, before the hub
/// takes on work it cannot finish. What survives — and what the first test below pins — is the
/// delivery ACCEPTED while the hub was <c>Started</c> whose turn runs after <c>Dispose</c> froze
/// creation. The gate is at intake; it cannot un-accept a delivery already in the queue.</para>
///
/// <para><b>The contract pinned here</b> is the same one #672 established one layer down: a
/// caller caught in a recycle window gets a TRANSIENT <see cref="ErrorType.ShuttingDown"/>
/// rejection — "the address may reactivate, ask again" — never a terminal fault, and never an
/// NRE. That classification is what keeps <c>SynchronizationStream</c>'s keep-alive and
/// change-feed resubscribe latch ALIVE so the page rehydrates after the recycle instead of staying
/// dead. A page is exactly what hits this: the overlay self-heal posts a self-<c>DisposeRequest</c>
/// (<c>NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal</c>) and any subscriber in that window landed
/// here — <c>OverlaySelfHealInstanceRecycleTest</c> failed 1-4 runs in 8 on exactly this. Both
/// doors are pinned below, because BOTH are ways a real page reaches a recycling area and the two
/// are answered by different code.</para>
/// </summary>
public class SubscribeDuringRecycleTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string StaticView = nameof(StaticView);
    private static readonly Address AreaAddress = new("area", "1");

    /// <summary>Accepted by the area hub and never answered — parks one pending response callback.</summary>
    private record HoldRequest : IRequest<HoldResponse>;

    private record HoldResponse;

    /// <summary>Occupies the area hub's single-threaded turn loop so the test controls what runs when.</summary>
    private record Blocker;

    // The subscribing side needs the data message contract (SubscribeRequest / SubscribeAck /
    // LayoutAreaReference) registered, exactly as a real client hub has it.
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData()
            .AddLayoutTypes()
            .WithTypes(typeof(HoldRequest), typeof(HoldResponse), typeof(Blocker));

    /// <summary>
    /// DOOR ONE — the delivery this hub ACCEPTED while it was healthy, whose turn runs after
    /// <c>Dispose</c> froze hosted-hub creation. The intake gate (#3506) cannot help here by
    /// construction: the delivery was already in the queue when the door closed. It must reach the
    /// stream-creation refusal and come back as a transient, named NACK — never the NRE.
    /// </summary>
    [HubFact]
    public async Task AnAcceptedSubscribe_ThatRunsAfterCreationFroze_IsNackedTransiently()
    {
        // 🚨 No hand-woven gate: the handler→test signal is an AsyncSubject the producer completes;
        // the release travels INTO the deliberately parked turn, so it is a volatile flag polled
        // under a bounded SpinUntil and written in the `finally` below.
        var blockerEntered = new AsyncSubject<Unit>();
        var releaseBlocker = 0;

        var host = GetHost();
        var area = await StartAreaHub(host, blockerEntered, () => Volatile.Read(ref releaseBlocker) == 1);

        try
        {
            // 1. Park the turn loop, so nothing the test posts below can run until it says so.
            host.Post(new Blocker(), o => o.WithTarget(AreaAddress));
            await blockerEntered.Should().Within(TestTimeouts.Convergence).Emit(
                "the blocker must be holding the area hub's turn loop");

            // 2. Dispose. Creation freezes SYNCHRONOUSLY here — on Dispose's first statement — and
            //    the ShutdownRequest it posts queues BEHIND the blocker, so RunLevel is still
            //    Started. That pairing is the whole window, and it is why the intake gate cannot
            //    be the answer to this door.
            //    (Production reaches the same line through the overlay self-heal's DisposeRequest;
            //    its handler calls exactly this. Called directly because a parked turn loop cannot
            //    handle a message.)
            area.Dispose();
            area.RunLevel.Should().Be(MessageHubRunLevel.Started,
                "the ShutdownRequest is queued behind the blocker, so the level has not moved — "
                + "if it had, this test would be exercising the intake gate instead");

            // 3. The subscribe ARRIVES while RunLevel is Started, so the intake gate admits it —
            //    exactly as it does for a healthy hub — and it queues behind the shutdown.
            var requestId = Guid.NewGuid().ToString("N");
            var ack = host
                .Observe(
                    (object)new SubscribeRequest(Guid.NewGuid().ToString("N"), new LayoutAreaReference(StaticView)),
                    o => o.WithTarget(AreaAddress),
                    requestId)!
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);

            // 🚨 "Accepted" is OBSERVED, never assumed. Releasing the blocker before the subscribe
            // has been enqueued would let the ShutdownRequest run first, move RunLevel to
            // Quiescing, and hand this test the INTAKE refusal — a correct answer to a different
            // question, and a flake decided by whether the host's routing turn won a race.
            await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
                .Select(_ => host.DescribeRequestFate(requestId))
                .Where(trail => trail.Contains($"ENQUEUED@{AreaAddress}", StringComparison.Ordinal))
                .FirstAsync()
                .Timeout(TestTimeouts.Convergence)
                .Await(TestContext.Current.CancellationToken);

            // 4. Release. The queue drains in order: shutdown, then our subscribe — whose handler
            //    now cannot create the stream's sub-hub.
            Volatile.Write(ref releaseBlocker, 1);

            var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => ack);
            Output.WriteLine($"NACK: errorType={failure.Failure?.ErrorType} message={failure.Failure?.Message}");

            failure.Failure.Should().NotBeNull();
            failure.Failure!.ErrorType.Should().Be(ErrorType.ShuttingDown,
                "a subscriber caught in a recycle window must get a RETRYABLE answer — the address "
                + "reactivates on the next access. Terminal classification kills the sync stream's "
                + "resubscribe latch and the page never comes back.");
            failure.Failure.Message.Should().Contain(nameof(HubDisposingException),
                "the failure must name the real cause — the host could not host the stream's "
                + "sub-hub. This also proves the test exercised the STREAM-CREATION refusal and was "
                + "not answered by MessageService's intake / deferred-queue NACKs, which carry a "
                + "different banner and are a different, already-fixed defect");
            failure.Failure.Message.Should().NotContain(nameof(NullReferenceException),
                "the pre-fix symptom: SynchronizationStream handed LayoutAreaHost a stream whose "
                + "non-nullable Hub was null, and the ctor NRE'd on the very next line");
        }
        finally
        {
            Volatile.Write(ref releaseBlocker, 1);
        }
    }

    /// <summary>
    /// DOOR TWO — the subscribe that merely ARRIVES while the hub is draining its quiesce. Before
    /// MeshWeaver#3506 this reached the layout stack too, and was answered by the same
    /// stream-creation refusal; the hub took on work it had no drain left to finish. It is now
    /// turned away at the door, and the answer a subscriber acts on is unchanged in the way that
    /// matters: a transient <see cref="ErrorType.ShuttingDown"/> rejection minted by the OWNER, so
    /// the sync stream's resubscribe latch survives and the page rehydrates.
    /// </summary>
    [HubFact]
    public async Task ASubscribeArrivingDuringTheQuiesce_IsRefusedAtTheDoor_AndStillTransiently()
    {
        var host = GetHost();
        var area = await StartAreaHub(host, blockerEntered: null, release: null);

        // Park an un-answered callback so the Quiescing drain cannot complete: the hub then stays
        // in the disposal window for its whole quiesce budget instead of racing through teardown
        // in under a millisecond.
        using var held = area
            .Observe<HoldResponse>(new HoldRequest(), o => o.WithTarget(AreaAddress))
            // Never throw from these callbacks — they run on the hub's scheduler, where an
            // exception would be unobserved. If the hold ever resolved, WaitForDisposalWindow
            // below would find the hub already Dead and fail the test with a clear message.
            .Subscribe(
                d => Output.WriteLine($"Hold callback answered unexpectedly: {d.Message}"),
                ex => Output.WriteLine($"Hold callback released: {ex.GetType().Name}: {ex.Message}"));

        // THE RECYCLE — the same self-DisposeRequest the overlay self-heal watcher posts.
        host.Post(new DisposeRequest(), o => o.WithTarget(AreaAddress));
        await WaitForDisposalWindow(area);

        var ack = host
            .Observe<SubscribeAck>(
                new SubscribeRequest(Guid.NewGuid().ToString("N"), new LayoutAreaReference(StaticView)),
                o => o.WithTarget(AreaAddress))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => ack);
        Output.WriteLine($"NACK: errorType={failure.Failure?.ErrorType} message={failure.Failure?.Message}");

        failure.Failure.Should().NotBeNull();
        failure.Failure!.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "what the subscriber acts on is unchanged by #3506 — the recycle window must still read "
            + "'ask again at the fresh activation', never 'gone'");
        ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, AreaAddress).Should().BeTrue(
            "the refusal must still be recognisable as THIS OWNER's answer rather than the routing "
            + "layer's, which is what tells a re-probe loop 'this address is coming back'");
        failure.Failure.Message.Should().Contain($"RunLevel={MessageHubRunLevel.Quiescing}",
            "this is the #3506 intake gate: the request was refused at the door DURING the drain, "
            + "not accepted and then failed inside the layout stack");
        failure.Failure.Message.Should().NotContain(nameof(NullReferenceException),
            "the original symptom must stay gone by every route into the window");
    }

    /// <summary>
    /// A hub that serves a layout area, fully started before the race so a subscribe can NOT be
    /// answered by the already-fixed deferred-behind-an-init-gate path (#672).
    /// </summary>
    private async Task<IMessageHub> StartAreaHub(
        IMessageHub host, AsyncSubject<Unit>? blockerEntered, Func<bool>? release)
    {
        var area = host.GetHostedHub(
            AreaAddress,
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse), typeof(Blocker))
                .WithHandler<HoldRequest>((_, d) => d.Processed())
                .WithHandler<Blocker>((h, d) =>
                {
                    blockerEntered?.OnNext(Unit.Default);
                    blockerEntered?.OnCompleted();
                    if (release is not null)
                        SpinWait.SpinUntil(() => release() || h.RunLevel >= MessageHubRunLevel.ShutDown,
                            TimeSpan.FromSeconds(60));
                    return d.Processed();
                })
                .AddLayout(layout => layout.WithView(StaticView, Controls.Html("Hello")))
                // Plumbing fixture, no logged-in user: post as infrastructure, exactly like
                // HubTestBase does for its own host/client hubs (never-null AccessContext).
                .WithPostingIdentity(PostingIdentity.System));
        area.Should().NotBeNull();
        await area!.Started.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return area;
    }

    /// <summary>
    /// Waits until <paramref name="hub"/> has demonstrably entered the disposal window
    /// (<see cref="MessageHubRunLevel.Quiescing"/> or later). Polling a PUBLIC state property, not
    /// a sleep: the test acts on a verified state rather than hoping to hit a race.
    /// </summary>
    private static async Task WaitForDisposalWindow(IMessageHub hub)
    {
        for (var i = 0; i < 200 && hub.RunLevel < MessageHubRunLevel.Quiescing; i++)
            await Task.Delay(10);
        (hub.RunLevel >= MessageHubRunLevel.Quiescing).Should().BeTrue(
            "the DisposeRequest must have moved the hub into its teardown phases — without that "
            + $"this test never exercises the window it exists to pin (RunLevel={hub.RunLevel})");
        (hub.RunLevel < MessageHubRunLevel.DisposeHostedHubs).Should().BeTrue(
            "past DisposeHostedHubs the intake gate's second tier rejects everything with a "
            + $"different RunLevel in its banner, so the assertion below would be about the wrong "
            + $"tier (RunLevel={hub.RunLevel})");
    }
}
