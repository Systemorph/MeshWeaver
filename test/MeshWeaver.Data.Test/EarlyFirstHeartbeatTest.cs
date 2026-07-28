using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the EARLY FIRST HEARTBEAT — the fix for the memex 2026-07-27 outage.
///
/// <para><b>What was measured.</b> From the compile activity log's microsecond timestamps: source
/// discovery took <b>45.20s</b> on a healthy mesh and <b>90.19s</b> (two misses) during the outage,
/// against <b>2.83s</b> of actual Roslyn work. 45.20s is one
/// <see cref="SyncStreamOptions.HeartbeatInterval"/> to within 200ms. Types then crossed the 60s
/// settle window and every plugin root served the "did not settle" fallback; the same silence keeps
/// an instance's NodeType stream from ever seeing a fresh build, which is why the overlay self-heal
/// had nothing to fire on and only a process restart cleared it. One bug, three symptoms.</para>
///
/// <para><b>The tell is WHERE the data arrives</b> — exactly on a heartbeat. The owner is not slow;
/// it holds the delivery until something pokes it, and every other recovery path in
/// <c>CreateExternalClient</c> is event-driven, so a stream whose owner acked and then went quiet
/// produces no event at all. Starting the existing heartbeat early is therefore the whole fix: the
/// same fire-and-forget <c>[SystemMessage]</c> the stream already sends forever, just not withheld
/// for a full interval first.</para>
///
/// <para><b>Why NOT a re-subscribe probe.</b> An earlier attempt re-sent the SubscribeRequest, and
/// each one creates a <c>sync/{ClientId}</c> hub on the owner's single-threaded action block — a
/// storm under mass cold start (i.e. every deploy), which is what
/// <see cref="ChangeFeedResubscribeCoalesceTest"/> exists to prevent. It fired on healthy streams on
/// loaded CI runners and perturbed unrelated tests in other shards. A heartbeat costs one message
/// and creates nothing, so it is safe on exactly the path that wedges owners.</para>
/// </summary>
public class EarlyFirstHeartbeatTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Production-length interval on purpose: with a 45s cadence, ONLY the early first tick can
    /// deliver a heartbeat inside this test's lifetime. On the old <c>Observable.Interval</c>
    /// schedule the count stays 0 — a deterministic RED, not a timing race.
    /// </summary>
    private static readonly TimeSpan ProductionHeartbeat = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long the test waits for the early beat before giving up. Generous for loaded CI runners,
    /// yet far below <see cref="ProductionHeartbeat"/> — so a heartbeat seen inside it can ONLY have
    /// come from the early first tick, and the assertion stays a detector rather than a stopwatch.
    /// </summary>
    private static readonly TimeSpan HeartbeatDeadline = TimeSpan.FromSeconds(30);

    private int _heartbeats;
    private int _subscribeCount;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            // Passive counters (the ChangeFeedResubscribeCoalesceTest idiom): count, then return the
            // delivery UNPROCESSED so the framework's own handlers still run.
            .WithHandler<HeartBeatEvent>((_, delivery) =>
            {
                Interlocked.Increment(ref _heartbeats);
                return delivery;
            })
            .WithHandler<SubscribeRequest>((_, delivery) =>
            {
                Interlocked.Increment(ref _subscribeCount);
                return delivery;
            })
            .AddData(data => data.AddSource(src => src
                .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))
                .WithType<LineOfBusiness>(t => t.WithInitialData(TestData.LinesOfBusiness))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithServices(services => services
                .Configure<SyncStreamOptions>(o => o.HeartbeatInterval = ProductionHeartbeat))
            .AddData(data => data.AddHubSource(CreateHostAddress(),
                ds => ds.WithType<BusinessUnit>().WithType<LineOfBusiness>()));

    /// <summary>
    /// 🚨 THE FIX. A fresh subscription must be poked well before a full heartbeat interval — that
    /// poke is what delivers a withheld snapshot, and waiting 45s for it is the outage. With the
    /// production 45s cadence configured, a heartbeat inside this window can only come from the
    /// early first tick.
    /// </summary>
    [HubFact]
    public async Task FirstHeartbeat_ArrivesEarly_AndNeverResubscribes()
    {
        GetHost();
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        // Opening the remote stream is what starts the heartbeat.
        await workspace.GetObservable<BusinessUnit>()
            .Should().Within(10.Seconds())
            .Match(x => x.Count > 0, "the owner must serve the initial snapshot");

        var afterInitial = Volatile.Read(ref _subscribeCount);

        // POLL, never sleep-then-assert. A fixed delay measures the runner, not the schedule —
        // that is why the first version of this test passed locally and failed on CI. Polling to a
        // deadline keeps the detector exact: the deadline is far below the 45s cadence, so a
        // heartbeat seen here can ONLY be the early first tick. On the old Interval() schedule the
        // count stays 0 for the full 45s and this still fails.
        var deadline = DateTime.UtcNow + HeartbeatDeadline;
        while (Volatile.Read(ref _heartbeats) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(250);

        Volatile.Read(ref _heartbeats).Should().BeGreaterThan(0,
            $"the owner must be poked within {HeartbeatDeadline.TotalSeconds:0}s of subscribing — on "
            + "the old Interval() schedule the first poke was a full 45s away, which is exactly the "
            + "stall that took the site down (45.20s of every compile, against 2.83s of Roslyn)");

        // …and poking must never become a re-subscribe: each SubscribeRequest creates a
        // sync/{ClientId} hub on the owner's single-threaded action block.
        Volatile.Read(ref _subscribeCount).Should().Be(afterInitial,
            "starting the heartbeat early must not re-subscribe anything");
    }

    /// <summary>
    /// The early tick is a floor-lowering for LONG intervals, never a delay imposed on short ones.
    /// A caller configuring a 200ms heartbeat (HeartbeatFireAndForgetTest does) must still get its
    /// first tick at 200ms — taking the sooner of the two keeps every existing cadence unchanged.
    /// Pinned because getting this backwards silently slowed every short-interval stream, which is
    /// exactly how the first version of this change broke an unrelated heartbeat test.
    /// </summary>
    [Fact]
    public void EarlyTick_NeverDelaysAShorterConfiguredInterval()
    {
        var shortInterval = TimeSpan.FromMilliseconds(200);
        var firstTick = shortInterval < JsonSynchronizationStream.FirstHeartbeat
            ? shortInterval
            : JsonSynchronizationStream.FirstHeartbeat;

        firstTick.Should().Be(shortInterval,
            "a 200ms cadence must fire at 200ms, not be pushed out to the early-tick floor");

        var longInterval = TimeSpan.FromSeconds(45);
        var longFirst = longInterval < JsonSynchronizationStream.FirstHeartbeat
            ? longInterval
            : JsonSynchronizationStream.FirstHeartbeat;

        longFirst.Should().Be(JsonSynchronizationStream.FirstHeartbeat,
            "a 45s cadence must be front-run by the early tick — that is the whole fix");
    }

    /// <summary>The early tick must sit far below the cadence it front-runs, and still be late
    /// enough not to fire before a healthy stream has even opened.</summary>
    [Fact]
    public void FirstHeartbeat_IsWellInsideTheInterval()
    {
        JsonSynchronizationStream.FirstHeartbeat.Should().BeLessThan(
            new SyncStreamOptions().HeartbeatInterval,
            "a first tick at or past the interval would leave the 45s stall exactly as it was");

        JsonSynchronizationStream.FirstHeartbeat.Should().BeGreaterThan(TimeSpan.FromSeconds(1),
            "poking instantly on every stream creation adds noise without fixing anything");
    }
}
