using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// <para><b>The window is not a millisecond fluke — it is structural.</b>
/// <c>MessageHub.Dispose</c> freezes hosted-hub creation SYNCHRONOUSLY, on its very first
/// statement (<c>HostedHubsCollection.CloseCreation</c>, which cascades through the whole
/// subtree), and only THEN posts the <c>ShutdownRequest</c> that moves <c>RunLevel</c> off
/// <c>Started</c>. Message intake, meanwhile, stays open until <c>DisposeHostedHubs</c> — the
/// whole <c>Quiescing</c> drain sits inside the gap. So a disposing hub happily ACCEPTS work it
/// can no longer perform, and serving a layout area means creating a hosted sub-hub for its
/// <c>SynchronizationStream</c>. The stream constructor used to paper over the refusal by
/// fabricating a "dead stream" with <c>Hub = null!</c>; <c>LayoutAreaHost</c> dereferenced
/// <c>Stream.Hub.ServiceProvider</c> on the next line.</para>
///
/// <para><b>How the window is held open</b> (no sleeps, no racing): one un-answered response
/// callback is parked on the area hub, so its <c>Quiescing</c> phase cannot drain and the hub
/// sits in the disposal window for its whole quiesce budget. The test then WAITS until
/// <c>RunLevel</c> has demonstrably reached <c>Quiescing</c> before subscribing — the state is
/// verified, not timed.</para>
///
/// <para><b>The contract pinned here</b> is the same one #672 established one layer down: a
/// caller caught in a recycle window gets a TRANSIENT <see cref="ErrorType.ShuttingDown"/>
/// rejection — "the address may reactivate, ask again" — never a terminal fault. That
/// classification is what keeps <c>SynchronizationStream</c>'s keep-alive and change-feed
/// resubscribe latch ALIVE so the page rehydrates after the recycle instead of staying dead.
/// A page is exactly what hits this: the overlay self-heal posts a self-<c>DisposeRequest</c>
/// (<c>NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal</c>) and any subscriber in that window
/// landed here — <c>OverlaySelfHealInstanceRecycleTest</c> failed 1-4 runs in 8 on exactly this.</para>
/// </summary>
public class SubscribeDuringRecycleTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string StaticView = nameof(StaticView);
    private static readonly Address AreaAddress = new("area", "1");

    /// <summary>Accepted by the area hub and never answered — parks one pending response callback.</summary>
    private record HoldRequest : IRequest<HoldResponse>;

    private record HoldResponse;

    // The subscribing side needs the data message contract (SubscribeRequest / SubscribeAck /
    // LayoutAreaReference) registered, exactly as a real client hub has it.
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData()
            .AddLayoutTypes()
            .WithTypes(typeof(HoldRequest), typeof(HoldResponse));

    [HubFact]
    public async Task SubscribeRacingDisposal_IsRejectedAsTransient_NotWithANullReference()
    {
        var host = GetHost();

        // A hub that serves a layout area, fully started before the race so the subscribe can
        // NOT be answered by the already-fixed deferred-behind-an-init-gate path (#672) — this
        // test must exercise the stream-creation refusal, not that one.
        var area = host.GetHostedHub(
            AreaAddress,
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse))
                .WithHandler<HoldRequest>((_, d) => d.Processed())
                .AddLayout(layout => layout.WithView(StaticView, Controls.Html("Hello")))
                // Plumbing fixture, no logged-in user: post as infrastructure, exactly like
                // HubTestBase does for its own host/client hubs (never-null AccessContext).
                .WithPostingIdentity(PostingIdentity.System));
        area.Should().NotBeNull();
        await area!.Started.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Park an un-answered callback so the Quiescing drain cannot complete: the hub then
        // stays in the disposal window (creation frozen, intake still open) for its whole
        // quiesce budget instead of racing through teardown in under a millisecond.
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
            .ToTask(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => ack);
        Output.WriteLine($"NACK: errorType={failure.Failure?.ErrorType} message={failure.Failure?.Message}");

        failure.Failure.Should().NotBeNull();
        failure.Failure!.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "a subscriber caught in a recycle window must get a RETRYABLE answer — the address "
            + "reactivates on the next access. Terminal classification kills the sync stream's "
            + "resubscribe latch and the page never comes back.");
        failure.Failure.Message.Should().Contain(nameof(HubDisposingException),
            "the failure must name the real cause — the host could not host the stream's sub-hub. "
            + "This also proves the test exercised the STREAM-CREATION refusal and was not "
            + "answered by MessageService's intake / deferred-queue NACKs, which carry a "
            + "different banner and are a different, already-fixed defect");
        failure.Failure.Message.Should().NotContain(nameof(NullReferenceException),
            "the pre-fix symptom: SynchronizationStream handed LayoutAreaHost a stream whose "
            + "non-nullable Hub was null, and the ctor NRE'd on the very next line");
    }

    /// <summary>
    /// Waits until <paramref name="hub"/> has demonstrably entered the disposal window
    /// (<see cref="MessageHubRunLevel.Quiescing"/> or later) — creation frozen, message intake
    /// still open. Polling a PUBLIC state property, not a sleep: the test acts on a verified
    /// state rather than hoping to hit a race.
    /// </summary>
    private static async Task WaitForDisposalWindow(IMessageHub hub)
    {
        for (var i = 0; i < 200 && hub.RunLevel < MessageHubRunLevel.Quiescing; i++)
            await Task.Delay(10);
        (hub.RunLevel >= MessageHubRunLevel.Quiescing).Should().BeTrue(
            "the DisposeRequest must have moved the hub into its teardown phases — without that "
            + $"this test never exercises the window it exists to pin (RunLevel={hub.RunLevel})");
        (hub.RunLevel < MessageHubRunLevel.DisposeHostedHubs).Should().BeTrue(
            "past DisposeHostedHubs the message service's own intake gate rejects everything, so "
            + $"the subscribe would never reach the layout stack (RunLevel={hub.RunLevel})");
    }
}
