using System;
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
/// Pins that the sync-stream keep-alive <see cref="HeartBeatEvent"/> is <b>fire-and-forget</b>: it is
/// posted to keep the owner grain alive and its delivery is NEVER observed.
///
/// <para><b>Why this matters (the wedge this replaces).</b> The heartbeat used to <c>Post</c> AND
/// <c>Observe</c> the delivery — the Observe existed to catch a terminal <c>NotFound</c> and tear the
/// keep-alive down for a permanently-gone owner. But a HEALTHY owner never acks a heartbeat, so that
/// Observe registered a hub callback that never resolved: the Rx <c>.Timeout</c> completed the
/// observable but left the underlying callback pending on the (cache) hub. Across many live sync
/// streams those leaked callbacks piled up (hundreds pending &gt;30s — the <c>[STALE-CALLBACK]</c>
/// scan) until the hub's action block / liveness probe stalled — the doc-crawl / atioz cache-hub
/// wedge. The fix: fire-and-forget. An undeliverable heartbeat is <c>[CanBeIgnored]</c>, so routing
/// drops it WITHOUT a NACK (<c>RoutingServiceBase.PostNotFound</c> AND
/// <c>RoutingGrain.PostFailureToSender</c> both skip <c>[CanBeIgnored]</c>) — there is no NotFound
/// storm to guard against and nothing to observe. Recycled/restarted owners are re-detected by the
/// change-feed resubscribe (the sole recycled-grain detector now).</para>
///
/// <para><b>The deterministic pin.</b> Subscribe to a live owner so the heartbeat runs; then flip the
/// owner "dead" so every further heartbeat comes back <c>NotFound</c>. Because the heartbeat is
/// fire-and-forget, that NotFound is IGNORED: the keep-alive is NOT torn down and heartbeats KEEP
/// flowing (the count keeps climbing). The OLD observe-and-stop behaviour would freeze the count — so
/// this assertion is the exact inverse, and would go RED if the leaky Observe were reintroduced.</para>
/// </summary>
public class HeartbeatFireAndForgetTest(ITestOutputHelper output) : HubTestBase(output)
{
    // Far shorter than the observation window, so many heartbeats tick within it.
    private static readonly TimeSpan ShortHeartbeat = TimeSpan.FromMilliseconds(200);

    private int _heartbeatCount;
    private volatile bool _ownerDead;

    public record GhostData(string Id);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            // Count every heartbeat. Once "dead", reply NotFound to it — the shape a router returns
            // when the owner is gone. A fire-and-forget heartbeat must IGNORE that reply (no Observe),
            // so the reply changes nothing: the timer keeps ticking.
            .WithHandler<HeartBeatEvent>((hub, delivery) =>
            {
                Interlocked.Increment(ref _heartbeatCount);
                if (_ownerDead)
                    hub.Post(
                        new DeliveryFailure(delivery) { ErrorType = ErrorType.NotFound, Message = "owner gone" },
                        o => o.ResponseFor(delivery));
                return delivery.Processed();
            })
            .AddData(data => data.AddSource(src => src
                .WithType<GhostData>(t => t.WithInitialData(
                    _ => Observable.Return(new[] { new GhostData("import-fingerprint") }.AsEnumerable())))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithServices(services =>
                services.Configure<SyncStreamOptions>(o => o.HeartbeatInterval = ShortHeartbeat))
            .AddData(data => data.AddHubSource(CreateHostAddress(), ds => ds.WithType<GhostData>()));

    [HubFact]
    public async Task HeartbeatIsFireAndForget_NotFoundReplyIsIgnored_NoTearDownNoLeak()
    {
        GetHost();
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        // 1. Subscribe succeeds → the keep-alive heartbeat is ticking against a live owner.
        await workspace.GetObservable<GhostData>("import-fingerprint")
            .Where(d => d is not null)
            .Should().Within(15.Seconds())
            .Match(d => d!.Id == "import-fingerprint");

        // 2. Confirm heartbeats are flowing before we kill the owner.
        await Observable.Interval(50.Milliseconds())
            .Select(_ => Volatile.Read(ref _heartbeatCount))
            .Should().Within(10.Seconds())
            .Match(c => c >= 2);

        // 3. Owner DIES — every further heartbeat comes back NotFound.
        var countAtDeath = Volatile.Read(ref _heartbeatCount);
        _ownerDead = true;

        // 4. Fire-and-forget: the NotFound is ignored (never observed), so the keep-alive is NOT torn
        //    down — it KEEPS flowing. (The endless post is benign: routing drops an undeliverable
        //    [CanBeIgnored] heartbeat without a NACK, so there is no storm and no leaked callback.)
        //    The OLD observe-and-stop code would freeze this count — the exact inverse.
        Thread.Sleep(TimeSpan.FromSeconds(2));
        var countAfter = Volatile.Read(ref _heartbeatCount);

        (countAfter - countAtDeath).Should().BeGreaterThanOrEqualTo(3,
            "the heartbeat is fire-and-forget — a NotFound reply is ignored, so the keep-alive keeps " +
            "ticking rather than observing the reply (which is what leaked callbacks and wedged the hub)");
    }
}
