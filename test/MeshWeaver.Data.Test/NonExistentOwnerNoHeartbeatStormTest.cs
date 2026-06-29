using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the framework guarantee behind "we must not have endless loops": a sync
/// subscription opened against a NON-EXISTENT owner must fail fast AND tear down its
/// keep-alive — it may NOT keep heart-beating the missing owner forever.
///
/// <para>Before the fix, <c>JsonSynchronizationStream</c> faulted subscribers on the
/// initial <c>SubscribeRequest</c> NotFound (<c>reduced.OnError</c>) but left the
/// stream UNDISPOSED — so the 45s <c>HeartBeatEvent</c> timer (registered on the
/// stream's DISPOSAL, not its error) kept posting to the missing owner forever. One
/// zombie subscription per open; multiplied by Blazor re-render fan-out that re-opens
/// absent paths, it ramped into the resubscribe storm that pinned the CPU. The fix
/// disposes the heartbeat + resubscribe (a CompositeDisposable) the moment the initial
/// SubscribeRequest comes back as a terminal <see cref="DeliveryFailure"/>.</para>
///
/// <para>The test makes the zombie observable: a SHORT heartbeat + an owner that FAILS
/// every <see cref="SubscribeRequest"/> (NotFound) and COUNTS the heartbeats it
/// receives. After the failure, ZERO heartbeats may arrive. Without the fix the count
/// climbs one-per-interval — a deterministic RED.</para>
/// </summary>
public class NonExistentOwnerNoHeartbeatStormTest(ITestOutputHelper output) : HubTestBase(output)
{
    // Far shorter than any plausible in-process SubscribeRequest round-trip (sub-ms to
    // low-ms), so WITH the fix the keep-alive is disposed before the first tick → 0
    // heartbeats; WITHOUT the fix the timer fires ~once per interval across the wait.
    private static readonly TimeSpan ShortHeartbeat = TimeSpan.FromMilliseconds(200);

    // Instance (never static — no cross-test bleed). Incremented by the owner's
    // HeartBeatEvent handler each time a heartbeat lands.
    private int _heartbeatCount;

    /// <summary>A type the owner has no reducer for — used only to shape the subscribe.</summary>
    public record GhostData(string Id);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            // The owner FAILS every SubscribeRequest with NotFound — the "address does not
            // exist for this reference" shape that drives the client's terminal teardown.
            .WithHandler<SubscribeRequest>((hub, delivery) =>
            {
                hub.Post(
                    new DeliveryFailure(delivery) { ErrorType = ErrorType.NotFound, Message = "ghost owner" },
                    o => o.ResponseFor(delivery));
                return delivery.Processed();
            })
            // …and COUNTS every heartbeat it receives. A correctly-torn-down subscription
            // sends none after the NotFound.
            .WithHandler<HeartBeatEvent>((hub, delivery) =>
            {
                Interlocked.Increment(ref _heartbeatCount);
                return delivery.Processed();
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithServices(services =>
                services.Configure<SyncStreamOptions>(o => o.HeartbeatInterval = ShortHeartbeat))
            .AddData(data => data.AddHubSource(CreateHostAddress(), ds => ds.WithType<GhostData>()));

    [HubFact]
    public async Task SubscribeToNonExistentOwner_FailsFast_AndStopsHeartbeat()
    {
        var host = GetHost();      // activate the owner hub
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        // Opening the remote stream posts the SubscribeRequest to the owner (which fails
        // NotFound) and sets up the heartbeat synchronously. The stream must surface the
        // failure as a TERMINAL OnError fast — never a forever-cold wait.
        var error = await workspace.GetObservable<GhostData>("ghost-id")
            .Materialize()
            .Should().Within(10.Seconds())
            .Match(n => n.Kind == NotificationKind.OnError,
                "a subscription to a non-existent owner must surface NotFound, not hang");
        error.Exception.Should().NotBeNull();

        // Negative assertion (sanctioned fixed wait: "confirm nothing happens"): well past
        // several heartbeat intervals, NO heartbeat may have reached the missing owner. The
        // terminal NotFound must have disposed the heartbeat. Without the fix the zombie
        // timer fires every ShortHeartbeat and this count climbs.
        Thread.Sleep(TimeSpan.FromSeconds(2));

        _heartbeatCount.Should().Be(0,
            "a terminal NotFound on the initial SubscribeRequest must tear down the heartbeat — " +
            "a non-existent owner must never be heart-beaten in an endless loop");
    }
}
