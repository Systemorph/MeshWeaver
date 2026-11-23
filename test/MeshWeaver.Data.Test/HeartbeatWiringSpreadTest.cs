using System;
using System.Diagnostics;
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
/// Systemorph/MeshWeaver#5532 — ties <see cref="HeartbeatSchedule"/> to the stream that beats.
/// <see cref="HeartbeatWaveSpreadTest"/> pins the arithmetic; this opens a REAL mirror and asserts its
/// first <c>HeartBeatEvent</c> reaches the owner on the schedule <c>HeartbeatSchedule.For</c> computes for
/// THAT stream id, so deleting or mis-wiring the call in <c>JsonSynchronizationStream</c> turns it red.
/// </summary>
public class HeartbeatWiringSpreadTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan First = TimeSpan.FromMilliseconds(1500);

    private long _firstBeatTicks;
    private string? _firstBeatStreamId;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithHandler<HeartBeatEvent>((_, delivery) =>
            {
                // The first beat that NAMES its stream (an acknowledged one) is the one whose schedule
                // we can recompute; an earlier unnamed one is ignored.
                if (delivery.Message.StreamId is { } id
                    && Interlocked.CompareExchange(ref _firstBeatTicks, Stopwatch.GetTimestamp(), 0) == 0)
                    _firstBeatStreamId = id;
                return delivery;
            })
            .AddData(data => data.AddSource(src => src
                .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))
                .WithType<LineOfBusiness>(t => t.WithInitialData(TestData.LinesOfBusiness))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithServices(services => services
                .Configure<SyncStreamOptions>(o =>
                {
                    o.HeartbeatInterval = Interval;
                    o.FirstHeartbeat = First;
                }))
            .AddData(data => data.AddHubSource(CreateHostAddress(),
                ds => ds.WithType<BusinessUnit>().WithType<LineOfBusiness>()));

    [HubFact]
    public async Task AMirrorBeatsOnItsOwnSpreadSchedule()
    {
        GetHost();
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        var opened = Stopwatch.GetTimestamp();
        await workspace.GetObservable<BusinessUnit>()
            .Should().Within(10.Seconds())
            .Match(x => x.Count > 0, "the owner must serve the initial snapshot");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (Volatile.Read(ref _firstBeatTicks) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Volatile.Read(ref _firstBeatTicks).Should().NotBe(0, "an acknowledged mirror must beat within its first tick");

        var observed = Stopwatch.GetElapsedTime(opened, Volatile.Read(ref _firstBeatTicks));
        var expected = HeartbeatSchedule.For(_firstBeatStreamId!, Interval, First).First;

        // Never later than configured plus scheduling slack, and no later than THIS stream's own spread
        // tick plus slack: an unspread mirror beats at the full 1.5 s, which fails whenever this
        // stream's expected tick is under ~0.9 s.
        observed.Should().BeLessThan(expected + TimeSpan.FromMilliseconds(600),
            $"stream {_firstBeatStreamId} must beat at its spread first tick ({expected.TotalMilliseconds:0} ms), "
            + "not at the configured 1500 ms");
    }
}
