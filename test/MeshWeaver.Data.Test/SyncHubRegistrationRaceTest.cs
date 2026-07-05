using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Deterministic repro + regression guard for the server-side subscription-lifecycle race that made
/// the React <c>/next</c> portal render a random subset of its regions / a blank first load.
///
/// <para>A subscriber receives a stream through a per-stream <c>sync/{id}</c> sub-hub created in the
/// <c>SynchronizationStream</c> ctor (<c>Host.GetHostedHub(sync/{id})</c>). The owner's FIRST
/// <c>Full</c> <see cref="DataChangedEvent"/> is routed to the subscriber and handled by
/// <c>DataExtensions.RouteStreamMessage</c>, which forwards it to that sub-hub. When the Full is
/// routed on a DIFFERENT action-block turn than the one registering <c>sync/{id}</c>, it can arrive
/// BEFORE the sub-hub is in the subscriber's <see cref="HostedHubsCollection"/>. Pre-fix the router
/// dropped it (<c>Dropping DataChangedEvent … no synchronization hub found</c>) → the region rendered
/// blank; different regions lost the race on different loads → "random subset".</para>
///
/// <para>The fix waits a bounded grace for <c>sync/{id}</c> to register (via
/// <see cref="HostedHubsCollection.HubAdded"/>) and re-delivers. This test posts the Full, drains the
/// subscriber's action block past its routing (so the miss is genuinely handled before the sub-hub
/// exists), THEN registers the sub-hub — and asserts the sub-hub receives the (held-then-replayed)
/// Full. FAILS pre-fix (the Full is dropped before step 3 registers the sub-hub).</para>
/// </summary>
public class SyncHubRegistrationRaceTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record Ping : IRequest<Pong>;
    public record Pong;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services =>
                // Generous grace so the (loaded-CI) gap between the Full and the sub-hub registration
                // never expires the hold in this positive test.
                services.Configure<SyncStreamOptions>(o => o.SyncHubRegistrationGrace = TimeSpan.FromSeconds(30)))
            .AddData()
            // A marker request to drain the subscriber's action block deterministically past the
            // Full's routing before the sub-hub is registered.
            .WithTypes(typeof(Ping), typeof(Pong))
            .WithHandler<Ping>((hub, request) =>
            {
                hub.Post(new Pong(), o => o.ResponseFor(request));
                return request.Processed();
            });

    [HubFact]
    public async Task FirstFull_ArrivingBeforeSyncHubRegistration_IsDelivered()
    {
        var subscriber = GetHost(); // a hosted hub with AddData ⇒ RouteStreamMessage
        var streamId = "race-" + Guid.NewGuid().AsString();

        // 1) The owner's first Full arrives for a stream whose sync sub-hub does not exist yet.
        subscriber.Post(
            new DataChangedEvent(streamId, 1, new RawJson("{}"), ChangeType.Full, null),
            o => o.WithTarget(subscriber.Address));

        // 2) Drain the subscriber's action block PAST the Full's routing (this is what makes the
        //    race deterministic: the miss is handled while the sub-hub is genuinely absent).
        (await subscriber.Observe(new Ping(), o => o.WithTarget(subscriber.Address))
            .Should().Within(10.Seconds()).Emit())
            .Message.Should().BeOfType<Pong>();

        // 3) NOW the subscriber registers its sync sub-hub (as GetRemoteStream's SynchronizationStream
        //    ctor does) — a plain receiver that captures whatever DataChangedEvent it is handed.
        var received = new ReplaySubject<DataChangedEvent>(1);
        using var syncHub = subscriber.GetHostedHub(
            SynchronizationAddress.Create(streamId),
            c => c.WithTypes(typeof(DataChangedEvent))
                .WithHandler<DataChangedEvent>((_, d) => { received.OnNext(d.Message); return d.Processed(); }));

        // 4) The held Full must be re-delivered to the freshly-registered sub-hub — never dropped.
        var full = await received.Should().Within(10.Seconds()).Emit();
        full.Should().NotBeNull("the first Full must be delivered even when it raced ahead of the sync sub-hub");
        full!.StreamId.Should().Be(streamId);
    }
}
