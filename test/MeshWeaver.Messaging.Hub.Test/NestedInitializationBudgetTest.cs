using System;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins that an initialization bound nested inside another one is STRICTLY SMALLER, so the level
/// nearest a hang is the level that reports it (Systemorph/MeshWeaver#1122, #1186, #2886).
///
/// <para><b>The defect.</b> Initialization is a nested wait — a hub's <c>DataContext</c> time-box
/// waits on its data sources, each of which waits on the <c>sync/{clientId}</c> sub-hub serving its
/// stream, and that sub-hub is a hub with an initialization of its own. Every level took the same
/// independently-written constant, <b>120 s</b>, and nothing said the levels were supposed to be
/// ordered at all. Equal is not an ordering: the clocks are armed microseconds apart on different
/// action blocks, so which level reports was decided by scheduling. When the OUTER one wins, it
/// tears the inner level down as a recognized shutdown and the level that knew which wait starved
/// says nothing — the enclosing line can only ever report that initialization ran out of time. This
/// is the same defect <c>MeshOperationOptions</c> removed from the write path and <c>ReadBudget</c>
/// from the read path.</para>
///
/// <para><b>The fix under test.</b> Exactly one bound is configured per hub
/// (<c>WithStartupTimeout</c>, else <see cref="HubInitializationBudget.Root"/>); everything nested
/// inside it is derived by <see cref="HubInitializationBudget.Nest"/>, which is strictly
/// contracting. <b>No bound is widened or narrowed to make a symptom go away</b> — the root value
/// is untouched and only the derived rungs are new.</para>
/// </summary>
public class NestedInitializationBudgetTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record ProbeRequest : IRequest<ProbeResponse>;

    private record ProbeResponse;

    /// <summary>The hosted hub the host's own initialization waits on.</summary>
    private static readonly Address ChildAddress = new("nestedinitchild", "1");

    /// <summary>
    /// A hosted hub created AFTER the host has started — the shape of a per-node hub, which routing
    /// activates on demand. Nothing is waiting on it, so nothing encloses it.
    /// </summary>
    private static readonly Address OnDemandAddress = new("nestedinitondemand", "1");

    /// <summary>The host's rung 1. Everything below it is derived, never written.</summary>
    private static TimeSpan HostBudget => TestTimeouts.Quick;

    /// <summary>The child's only BuildupAction: never emits, never completes — the hang under test.</summary>
    private static IObservable<Unit> HangsForever(IMessageHub _) => Observable.Never<Unit>();

    /// <summary>
    /// The host's BuildupAction, and the nesting itself: it creates a hosted hub and does not
    /// signal until that hub reaches <see cref="MessageHubRunLevel.Started"/>. This is the shape a
    /// data-carrying hub has in production — <c>DataContext</c>'s time-box waits on data sources
    /// that wait on their <c>sync/</c> sub-hubs — with the data layer removed so the test measures
    /// the BOUNDS and nothing else.
    /// </summary>
    private static IObservable<Unit> WaitsForTheHostedChild(IMessageHub hub) =>
        Observable.Defer(() =>
        {
            var child = hub.GetHostedHub(ChildAddress, c => c.WithInitialization(HangsForever));
            return child.RunLevelChanged
                .Where(level => level >= MessageHubRunLevel.Started)
                .Take(1)
                .Select(_ => Unit.Default);
        });

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithStartupTimeout(HostBudget)
            .WithTypes(typeof(ProbeRequest), typeof(ProbeResponse))
            .WithHandler<ProbeRequest>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .WithInitialization(WaitsForTheHostedChild);

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithTypes(typeof(ProbeRequest), typeof(ProbeResponse));

    /// <summary>
    /// The structural half: every step of the ladder contracts, and an explicitly configured bound
    /// still wins for the hub it is set on.
    /// </summary>
    [HubFact]
    public async Task EveryStepOfTheLadderIsStrictlySmallerThanTheOneItIsNestedIn()
    {
        // The derivation itself, at the two scales that matter: one where the absolute reserve
        // decides, and one where the fraction floor does. Both must contract.
        HubInitializationBudget.Nest(HubInitializationBudget.Root)
            .Should().BeLessThan(HubInitializationBudget.Root,
                "a bound nested inside another one must be able to fire FIRST — it is the only one "
                + "that knows WHICH wait starved");
        HubInitializationBudget.Nest(HubInitializationBudget.NestingReserve)
            .Should().BeGreaterThan(TimeSpan.Zero,
                "the fraction floor keeps the ladder positive when the enclosing bound is at or "
                + "below the reserve — the short-bound shape tests configure")
            .And.BeLessThan(HubInitializationBudget.NestingReserve,
                "and strictly decreasing at that scale too, or the ladder collapses onto one tick");

        // The DOMAIN boundary, both sides. Below it the tick arithmetic stops being an ordering —
        // halving one tick truncates to zero, two rungs land on the same instant, and a timer at
        // TimeSpan.Zero fires at once. It is reachable by nesting alone (the fraction floor halves
        // per level), so the refusal is pinned rather than assumed.
        HubInitializationBudget.Nest(HubInitializationBudget.SmallestNestableBound)
            .Should().BeGreaterThan(TimeSpan.Zero, "the smallest nestable bound is IN the domain")
            .And.BeLessThan(HubInitializationBudget.SmallestNestableBound,
                "and the ladder still contracts strictly at the very bottom of it");
        Action belowTheDomain = () =>
            HubInitializationBudget.Nest(HubInitializationBudget.SmallestNestableBound - TimeSpan.FromTicks(1));
        belowTheDomain.Should().Throw<ArgumentOutOfRangeException>(
            "a bound the ladder cannot be shown to contract inside is refused, not silently "
            + "collapsed onto the rung enclosing it");

        var host = GetHost();
        // Settle the host first: the hosted hub is created BY the host's BuildupAction, so reading
        // the registry before the host answers reads it before there is anything to read — which is
        // a fact about the clock, not about the ladder.
        await GetClient()
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        host.Configuration.InitializationBudget.Should().Be(HostBudget,
            "an explicit WithStartupTimeout is the ONE configured value for this hub");
        host.Configuration.NestedInitializationBudget.Should()
            .BeLessThan(host.Configuration.InitializationBudget,
                "the BuildupAction Concat and the DataContext time-box run INSIDE the hub's own "
                + "initialization budget");

        var child = host.GetHostedHub(ChildAddress, HostedHubCreation.Never);
        child.Should().NotBeNull(
            "the host's BuildupAction creates it, so it exists by the time the host answers");
        child!.Configuration.InitializationBudget.Should()
            .BeLessThan(host.Configuration.NestedInitializationBudget,
                "the host's rung-2 wait is what waits on this hub, so this hub's WHOLE "
                + "initialization must give up strictly before that wait does");
        child.Configuration.NestedInitializationBudget.Should()
            .BeLessThan(child.Configuration.InitializationBudget,
                "and the ladder keeps contracting at every further level of nesting");

        // 🚨 The OTHER side of the discriminator, and the one that keeps a production bound where
        // it was: HOSTED is not ENCLOSED. A hub created once its host has reached Started is not
        // something the host's initialization can be waiting on — that is exactly a per-node hub,
        // which IS a hosted hub of the mesh root but is activated on demand long afterwards — so
        // it takes the full root budget and contracts nothing.
        var onDemand = host.GetHostedHub(OnDemandAddress, c => c);
        onDemand.Configuration.InitializationBudget.Should().Be(HubInitializationBudget.Root,
            "a hub nothing is waiting on takes the root budget, however it was created — "
            + "contracting it would narrow a bound for no reason at all");
    }

    /// <summary>
    /// The behavioural half, and the one the issue is about: the hub whose BuildupAction hangs
    /// reports the hang ITSELF, naming the action, and the hub that was waiting on it never has to
    /// guess — its own bound never fires.
    ///
    /// <para><b>RED with the derivation reverted</b> (a hosted hub back on the flat default): the
    /// child's bound is then far larger than the host's, so the HOST's bound fires first, the host
    /// latches FAILED and answers the probe with a <c>DeliveryFailure</c> naming only its own
    /// pending action — an anonymous "something below me hung".</para>
    /// </summary>
    [HubFact]
    public async Task AHungHostedHubReportsItself_AndTheHubWaitingOnItNeverGivesUp()
    {
        var host = GetHost();
        var client = GetClient();

        // The host ANSWERS — which is only possible if its own bound never fired, i.e. the level
        // below gave up first and let the host's wait complete. A positive signal, not a silence.
        var response = await client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        response.Message.Should().BeOfType<ProbeResponse>(
            "the host's initialization completed, so it serves requests normally");

        ((MessageHub)host).InitializationError.Should().BeNull(
            "the enclosing bound must NOT fire: it could only report that initialization ran out "
            + "of time, which is the attribution loss this ladder exists to prevent");

        var child = host.GetHostedHub(ChildAddress, HostedHubCreation.Never);
        child.Should().NotBeNull("the hub the host was waiting on is still hosted");
        var childError = ((MessageHub)child!).InitializationError;
        childError.Should().NotBeNull(
            "the level nearest the hang is the level that reports it");
        childError!.Message.Should().Contain(nameof(HangsForever),
            "and it names the BuildupAction that did not complete, which no enclosing bound can");
    }
}
