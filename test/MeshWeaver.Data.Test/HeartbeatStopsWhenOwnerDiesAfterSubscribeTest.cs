using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the OTHER half of "we must not heart-beat a dead owner forever" — the half that the
/// recurring <c>{Partition}/_Activity/import-{fingerprint}</c> storm slips through.
/// <see cref="NonExistentOwnerNoHeartbeatStormTest"/> covers the owner that NEVER existed (the
/// initial <c>SubscribeRequest</c> NotFounds → keep-alive torn down). This covers the owner that
/// existed at subscribe time and then <b>died</b> — exactly a one-shot import-activity lock: the
/// importer writes it via <c>GetMeshNodeStream(activityPath).Update</c> (a cached, heart-beated sync
/// stream), the dedicated off-router import hub is disposed when the import completes, and the node
/// persists as history so there is <b>no Created/Deleted change-feed event</b> to trigger the
/// change-feed resubscribe.
///
/// <para>Once heartbeats became fire-and-forget — with the change feed as the SOLE recycled-grain
/// detector — a subscription whose owner dies without a change-feed pulse keeps posting
/// <see cref="HeartBeatEvent"/> to the missing owner every interval forever → <c>[ROUTE] NotFound</c>
/// per partition, per interval, for the life of the silo. The fix: the heartbeat observes its
/// delivery and, on a terminal NotFound <see cref="DeliveryFailure"/>, tears the keep-alive down
/// (after a resubscribe attempt for a genuinely recycled grain).</para>
///
/// <para>The test makes it deterministic: the owner serves a real type so the subscribe SUCCEEDS and
/// the heartbeat runs; then a flag flips the owner "dead" so every further heartbeat comes back
/// NotFound — with NO change-feed event. After death the heartbeat count must FREEZE. Without the fix
/// it climbs one-per-interval — a deterministic RED.</para>
/// </summary>
public class HeartbeatStopsWhenOwnerDiesAfterSubscribeTest(ITestOutputHelper output) : HubTestBase(output)
{
    // Far shorter than the 2s observation window, so a still-alive heartbeat would tick ~10×.
    private static readonly TimeSpan ShortHeartbeat = TimeSpan.FromMilliseconds(200);

    private int _heartbeatCount;
    private volatile bool _ownerDead;

    /// <summary>Stand-in for the import-activity content; keyed by <see cref="Id"/>.</summary>
    public record GhostData(string Id);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            // Count every heartbeat. Once "dead", reply NotFound to it — the shape the router
            // returns when the owner grain/hub is gone — WITHOUT emitting any change-feed event,
            // so only the heartbeat-delivery path can tear the keep-alive down.
            .WithHandler<HeartBeatEvent>((hub, delivery) =>
            {
                Interlocked.Increment(ref _heartbeatCount);
                if (_ownerDead)
                    hub.Post(
                        new DeliveryFailure(delivery) { ErrorType = ErrorType.NotFound, Message = "owner gone" },
                        o => o.ResponseFor(delivery));
                return delivery.Processed();
            })
            // A REAL data source so the initial SubscribeRequest SUCCEEDS and the heartbeat starts.
            .AddData(data => data.AddSource(src => src
                .WithType<GhostData>(t => t.WithInitialData(
                    _ => Observable.Return(new[] { new GhostData("import-fingerprint") }.AsEnumerable())))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithServices(services =>
                services.Configure<SyncStreamOptions>(o => o.HeartbeatInterval = ShortHeartbeat))
            .AddData(data => data.AddHubSource(CreateHostAddress(), ds => ds.WithType<GhostData>()));

    [HubFact]
    public async Task OwnerDiesAfterSubscribe_NoChangeFeedEvent_HeartbeatTearsDown()
    {
        GetHost();   // activate the owner hub
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        // 1. Subscribe SUCCEEDS — the Initial arrives, so the keep-alive heartbeat is now ticking
        //    against a LIVE owner (distinct from the never-existed case).
        await workspace.GetObservable<GhostData>("import-fingerprint")
            .Where(d => d is not null)
            .Should().Within(15.Seconds())
            .Match(d => d!.Id == "import-fingerprint");

        // 2. Confirm the heartbeat is genuinely flowing to the live owner before we kill it.
        await Observable.Interval(50.Milliseconds())
            .Select(_ => Volatile.Read(ref _heartbeatCount))
            .Should().Within(10.Seconds())
            .Match(c => c >= 2);

        // 3. The owner DIES — every further heartbeat NotFounds, with NO change-feed event (the
        //    _Activity/import-* lock persists as history). Only the heartbeat-delivery path can save us.
        var countAtDeath = Volatile.Read(ref _heartbeatCount);
        _ownerDead = true;

        // 4. Sanctioned fixed wait ("confirm nothing happens"): several heartbeat intervals later the
        //    count must have FROZEN — the terminal NotFound tore the keep-alive down. Without the fix
        //    the zombie timer keeps posting and this climbs ~one-per-interval.
        Thread.Sleep(TimeSpan.FromSeconds(2));
        var countAfter = Volatile.Read(ref _heartbeatCount);

        (countAfter - countAtDeath).Should().BeLessThanOrEqualTo(1,
            "once a heartbeat comes back NotFound the keep-alive must tear down — a one-shot " +
            "{Partition}/_Activity/import-* lock must never be heart-beaten in an endless NotFound loop");
    }
}
