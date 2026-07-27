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
/// Pins the missing-<c>Initial</c> watchdog — and, more importantly, pins that it stays SILENT on
/// a healthy stream.
///
/// <para><b>Why it exists.</b> Every other recovery path in
/// <c>JsonSynchronizationStream.CreateExternalClient</c> is event-driven: the change feed announces
/// an owner restart and the latch re-subscribes. Nothing recovered a stream whose owner ACKED the
/// SubscribeRequest and then never delivered the first <c>DataChangedEvent</c> — no announce ever
/// comes, so the read just waited, in practice until the 45s heartbeat shook something loose.</para>
///
/// <para>Measured on memex 2026-07-27 from the compile activity log: source discovery took
/// <b>45.20s</b> on a healthy mesh and <b>90.19s</b> (two misses) during the outage, against 2.6s of
/// Roslyn. Types then crossed the 60s settle window and every plugin root served the "did not settle"
/// fallback. The same silence stopped instance NodeType streams from ever seeing a fresh build, which
/// is why the overlay self-heal had nothing to fire on and only a process restart cleared it.</para>
///
/// <para><b>The risk this test guards.</b> A watchdog that re-subscribes too eagerly is worse than
/// the bug: every sync stream in the mesh would re-send SubscribeRequests, and each one creates a
/// <c>sync/{ClientId}</c> hub on the owner's single-threaded action block — the exact storm
/// <see cref="ChangeFeedResubscribeCoalesceTest"/> exists to prevent. So the load-bearing assertion
/// here is the NEGATIVE one: a stream that receives its data normally must never re-subscribe, no
/// matter how long it is left alone.</para>
/// </summary>
public class MissingInitialResubscribeTest(ITestOutputHelper output) : HubTestBase(output)
{
    // Long enough that the heartbeat can never fire during the test — so every SubscribeRequest the
    // owner counts is the initial subscribe or a watchdog re-subscribe, nothing else.
    private static readonly TimeSpan LongHeartbeat = TimeSpan.FromMinutes(5);

    // Comfortably longer than the production probe (4s) so a mis-firing watchdog HAS to show up
    // within the wait; a correct one stays at exactly one subscribe.
    private static readonly TimeSpan QuietWatch = TimeSpan.FromSeconds(9);

    private int _subscribeCount;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            // Passive counter (same idiom as ChangeFeedResubscribeCoalesceTest): count, then return
            // the delivery UNPROCESSED so AddData still acks and sends the initial data.
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
                .Configure<SyncStreamOptions>(o => o.HeartbeatInterval = LongHeartbeat))
            .AddData(data => data.AddHubSource(CreateHostAddress(),
                ds => ds.WithType<BusinessUnit>().WithType<LineOfBusiness>()));

    /// <summary>
    /// 🚨 THE STORM GUARD. A stream that gets its initial snapshot must NEVER re-subscribe — the
    /// watchdog has to observe the delivery and stand down. If this regresses, every sync stream in
    /// the mesh re-sends SubscribeRequests on a timer and each one creates a sync hub on the owner's
    /// single action block; that wedges owners far more effectively than the bug being fixed.
    /// </summary>
    [HubFact]
    public async Task HealthyStream_ReceivesItsData_AndNeverResubscribes()
    {
        GetHost();
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        // The initial snapshot must actually arrive — otherwise the negative assertion below would
        // pass vacuously on a stream that is broken in a different way.
        await workspace.GetObservable<BusinessUnit>()
            .Should().Within(10.Seconds())
            .Match(x => x.Count > 0, "the owner must serve the initial snapshot");

        var afterInitial = Volatile.Read(ref _subscribeCount);
        afterInitial.Should().Be(1, "one subscribe opens the stream");

        // Sit idle well past the watchdog probe. A correct watchdog saw the delivery and stood down.
        await Task.Delay(QuietWatch);

        Volatile.Read(ref _subscribeCount).Should().Be(1,
            "a healthy stream must NEVER re-subscribe — a watchdog that fires anyway re-creates a "
            + "sync hub per stream on the owner's single action block, which is the wedge this "
            + "whole area exists to avoid");
    }

    /// <summary>
    /// The watchdog's budget is bounded and its probe sits far below the heartbeat — the two numbers
    /// that make it a missed-wakeup nudge rather than a retry loop. Pinned as values because the
    /// timing behaviour they govern is otherwise only observable in a multi-second integration run.
    /// </summary>
    [Fact]
    public void WatchdogBudget_IsBounded_AndProbesFarBelowTheHeartbeat()
    {
        JsonSynchronizationStream.MaxMissingInitialResubscribes.Should().BeInRange(1, 5,
            "this nudges a missed wakeup; an unbounded retry would be a storm");

        JsonSynchronizationStream.MissingInitialProbe.Should().BeLessThan(
            new SyncStreamOptions().HeartbeatInterval,
            "probing no earlier than the heartbeat would leave the 45s stall exactly as it was");

        JsonSynchronizationStream.MissingInitialProbe.Should().BeGreaterThan(TimeSpan.FromSeconds(1),
            "probing too eagerly would re-subscribe streams that are merely doing a cold read");
    }
}
