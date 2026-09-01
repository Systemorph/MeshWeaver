using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The WIRING half of issue #2638, on a real silo: a route leg <see cref="RoutingGrain"/> has
/// dispatched is counted on the silo's <see cref="RoutingQuiescence"/> from dispatch until it
/// terminates, and the silo's lifecycle actually carries <see cref="RoutingQuiescenceSiloParticipant"/>.
/// <see cref="RoutingQuiescenceTest"/> pins what the participant DOES with that count; this pins
/// that the grain feeds it and the silo registers it — the two things a pure test cannot see.
///
/// <para>Same vehicle as <see cref="RoutingGrainTurnIsolationTest"/>: the silo's
/// <see cref="IPathResolver"/> is decorated with one whose subscribe BLOCKS for a magic partition,
/// so a leg is provably in flight for as long as the test wants, and released explicitly. Every
/// wait is on the gauge's own emissions or on the decorator's counters.</para>
/// </summary>
public class RoutingQuiescenceClusterTest(ITestOutputHelper output)
    : OrleansMeshTestBase(output)
{
    /// <inheritdoc />
    protected override Type SiloConfiguratorType => typeof(StallingResolverSiloConfigurator);

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ADispatchedRouteLeg_IsInFlightOnTheSiloGauge_UntilItTerminates()
    {
        var silo = Cluster.SiloServices();
        var resolver = silo.GetRequiredService<IPathResolver>() as StallingPathResolver;
        resolver.Should().NotBeNull(
            "the stalling decorator must be the silo's IPathResolver — without it nothing is in flight "
            + "long enough to observe and this test would prove nothing");

        // The silo REGISTERS the hold: the gauge and its lifecycle participant are the silo's own.
        var quiescence = silo.GetRequiredService<RoutingQuiescence>();
        silo.GetServices<ILifecycleParticipant<ISiloLifecycle>>()
            .Should().Contain(p => p is RoutingQuiescenceSiloParticipant,
                "the silo lifecycle must carry the participant, or the count is measured and never acted on");

        var client = GetClient($"quiesce{Guid.NewGuid():N}"[..16]);
        try
        {
            // Precondition: nothing in flight before the delivery is dispatched.
            (await quiescence.InFlightChanges.FirstAsync().Await(Token())).Should().Be(0);

            client.Post(new PingRequest(),
                o => o.WithTarget(new Address(StallingPathResolver.StallPartition, "held-2638")));

            // The grain claimed the gauge at dispatch — before the leg even reached its subscribe.
            await quiescence.InFlightChanges.Where(n => n >= 1).FirstAsync().Await(Token());

            // …and the leg is provably parked inside its resolve (the standard interval-poll for a
            // source that is not itself observable).
            await Observable.Interval(TimeSpan.FromMilliseconds(25))
                .StartWith(0L)
                .Where(_ => resolver!.StallsEntered > 0)
                .FirstAsync()
                .Await(Token());

            (await quiescence.InFlightChanges.FirstAsync().Await(Token())).Should().BeGreaterThanOrEqualTo(1,
                "a leg parked in path resolution is accepted routing work the silo stop must wait for");
            Output.WriteLine("route leg in flight and stalled — the silo gauge reads it");
        }
        finally
        {
            // Unblock the stalled leg so no thread, no pool permit and no gauge slot survives into teardown.
            resolver!.Release();
        }

        // The leg terminates (NotFound → the sender is NACK'd) and the gauge returns to zero:
        // termination, not dispatch, is what releases the slot.
        await quiescence.InFlightChanges.Where(n => n == 0).FirstAsync().Await(Token());
        Output.WriteLine("route leg terminated — the silo gauge is idle again");
    }

    private static CancellationToken Token() => new CancellationTokenSource(Bound).Token;
}
