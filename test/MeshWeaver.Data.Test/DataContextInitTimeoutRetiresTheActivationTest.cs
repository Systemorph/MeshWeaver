using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Systemorph/MeshWeaver#1122 — a hub that demand routing re-creates
/// (<see cref="MessageHubConfiguration.WithReactivationOnDemand"/>, i.e. every per-node hub) whose
/// <see cref="DataContext"/> initialization TIMES OUT must not latch FAILED for the life of the
/// process. The activation is retired (disposed, loudly), and the NEXT ACCESS re-creates it.
///
/// <para>Three things are pinned, each red on the latch this replaced:</para>
/// <list type="number">
/// <item>The timed-out activation is GONE, the parked requester is told why in a TERMINAL answer,
/// and the next access is served by a fresh activation whose initialization ran again.</item>
/// <item>The CONTROL: without the marker the same time-out keeps today's latch — the marker, not
/// the time-out, selects the retirement.</item>
/// <item>No storm: a hot caller hammering a PERMANENTLY stuck address costs one activation per
/// time-box, never one per request, and nothing re-creates the address without an access.</item>
/// </list>
///
/// <para>The time-box is shortened to <see cref="TimeBox"/> (production: the hub's nested
/// initialization rung, ~115 s). Every assertion reads a fact the run produced — an answer, a
/// run level, a count of activations — never an elapsed time.</para>
/// </summary>
public class DataContextInitTimeoutRetiresTheActivationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly TimeSpan TimeBox = TimeSpan.FromMilliseconds(1500);

    private record Item(string Id);

    // NOT PingRequest: the DataContextInit gate exempts liveness pings, so only a real request
    // parks behind it.
    private record ProbeRequest : IRequest<ProbeResponse>;

    private record ProbeResponse;

    private bool reactivatesOnDemand = true;
    private bool neverRecovers;
    private int attempts;
    private ImmutableList<IMessageHub> activations = ImmutableList<IMessageHub>.Empty;

    /// <summary>Emits every activation of the host address as its BuildupActions run — hot, so a
    /// subscriber sees only activations created after it subscribed.</summary>
    private readonly Subject<IMessageHub> activated = new();

    /// <summary>
    /// The initial load of the host's one data source: the FIRST activation's never answers (the
    /// stall the time-box exists for); later ones answer at once unless <see cref="neverRecovers"/>
    /// — the permanently stuck dependency the storm case needs.
    /// </summary>
    private IObservable<IEnumerable<Item>> InitialLoad() => Observable.Defer(() =>
        Interlocked.Increment(ref attempts) == 1 || neverRecovers
            ? Observable.Never<IEnumerable<Item>>()
            : Observable.Return<IEnumerable<Item>>([new Item("one")]));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        var c = configuration
            .WithTypes(typeof(ProbeRequest), typeof(ProbeResponse))
            // The handler WOULD answer, so a served request can only come from an activation
            // whose gate opened — never from a latched one.
            .WithHandler<ProbeRequest>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .WithInitialization(hub =>
            {
                ImmutableInterlocked.Update(ref activations, list => list.Add(hub));
                activated.OnNext(hub);
                return Observable.Return(Unit.Default);
            })
            .AddData(data => data
                .WithInitializationTimeout(TimeBox)
                .AddSource(src => src.WithType<Item>(t => t
                    .WithKey(i => i.Id)
                    .WithInitialData(InitialLoad))));
        return reactivatesOnDemand ? c.WithReactivationOnDemand() : c;
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithTypes(typeof(ProbeRequest), typeof(ProbeResponse));

    /// <summary>
    /// Posts one request to the host address. The address is NOT resolved first: the delivery
    /// itself activates the host, so it is queued at that activation before the activation's
    /// initialization turn arms the time-box — the request is in flight when the box expires by
    /// construction, not by winning a race against the test thread.
    /// </summary>
    private Task<IMessageDelivery<ProbeResponse>> Ask(IMessageHub client, CancellationToken ct)
        => client.Observe(new ProbeRequest(), o => o.WithTarget(CreateHostAddress()))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    [Fact(Timeout = 120_000)]
    public async Task ATimedOutInit_RetiresTheActivation_AndTheNextAccessIsServedByAFreshOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();

        // 1. The requester parked behind the stuck activation gets a TERMINAL answer naming the
        //    time-box and the retirement — not the transient "is shutting down" banner, which every
        //    re-ask latch would ride into an automatic retry loop.
        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => Ask(client, ct));
        Output.WriteLine($"first activation answered: errorType={failure.Failure!.ErrorType} message={failure.Failure.Message}");
        failure.Failure.ErrorType.Should().Be(ErrorType.Failed,
            "the retirement answers the backlog terminally: only a NEW access may re-create the address");
        failure.Failure.Message.Should().Contain("did not complete within",
            "the answer names the time-box that expired");
        failure.Failure.Message.Should().Contain("retired",
            "the answer says the activation was retired, not latched");
        failure.Failure.Message.Should().NotContain("is shutting down",
            "a shutdown banner is matched as TRANSIENT by the mesh's classifiers and would be re-asked by itself");

        // 2. That activation is GONE — not parked Started with an InitializationError.
        activations.Should().HaveCount(1);
        var first = activations[0];
        await first.DisposalCompleted.FirstOrDefaultAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        first.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "a timed-out activation of an on-demand hub is retired, not latched FAILED for the process");

        // 3. The next ACCESS activates a fresh hub whose initialization runs again — and is served.
        var served = await Ask(client, ct);
        served.Message.Should().BeOfType<ProbeResponse>(
            "the address came back on the next access — the FAILED latch made this impossible until a restart");
        activations.Should().HaveCount(2, "the next access re-created the address on a new hub");
        activations[1].Should().NotBeSameAs(first);
        Volatile.Read(ref attempts).Should().Be(2,
            "the initialization ran once per activation: the stuck one and the fresh one");
    }

    /// <summary>
    /// The CONTROL: without <see cref="MessageHubConfiguration.WithReactivationOnDemand"/> the same
    /// time-out keeps the latch. Nothing would re-create such a hub if it were retired (the root
    /// mesh hub; a sub-hub owned by a live stream), so the latch is the honest answer for it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task WithoutReactivationOnDemand_TheSameTimeOutKeepsTheFailedLatch()
    {
        reactivatesOnDemand = false;
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => Ask(client, ct));
        Output.WriteLine($"latched: errorType={failure.Failure!.ErrorType} message={failure.Failure.Message}");
        failure.Failure.ErrorType.Should().Be(ErrorType.Failed);
        failure.Failure.Message.Should().Contain("initialization failed");

        var host = activations.Should().ContainSingle().Subject;
        host.RunLevel.Should().Be(MessageHubRunLevel.Started, "a latched hub stays, refusing");
        host.GetWorkspace().DataContext.InitializationError.Should().BeOfType<TimeoutException>(
            "the FAILED marker is recorded on the hub that stays");

        // A second access is refused by the SAME activation — the latch, measured.
        await Assert.ThrowsAsync<DeliveryFailureException>(() => Ask(client, ct));
        activations.Should().HaveCount(1, "nothing re-created the latched address");
        Volatile.Read(ref attempts).Should().Be(1, "nothing re-ran the initialization");
    }

    /// <summary>
    /// The storm guard. A PERMANENTLY stuck dependency under a hot caller: a burst of requests
    /// lands on ONE activation (activation is single-flight per address, and every delivery parks
    /// behind the one gate), all are answered when its time-box expires, and the address stays
    /// down until somebody accesses it again — there is no timer and no re-ask. The next burst
    /// costs exactly one more activation. So re-creation is paced by the time-box and driven by
    /// access, never by the caller's request rate.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AHotCallerOnAPermanentlyStuckAddress_CostsOneActivationPerTimeBox_AndNothingRetriesOnItsOwn()
    {
        const int burst = 25;
        neverRecovers = true;
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();

        await AssertBurstIsRefused(client, burst, ct);
        activations.Should().HaveCount(1,
            $"{burst} concurrent requests parked behind ONE activation's gate — none minted a hub of its own");
        await activations[0].DisposalCompleted.FirstOrDefaultAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        // With no access, nothing re-creates the address: no timer, no background retry, no
        // re-ask ridden in on a transient answer. Three time-boxes of silence is three
        // re-creations a retry loop would have made.
        await activated.Should().NotEmit(TimeBox * 3,
            "a retired address is re-created by the next ACCESS only", ct);
        activations.Should().HaveCount(1);

        // The next burst is an access: exactly one more activation, refused the same way.
        await AssertBurstIsRefused(client, burst, ct);
        activations.Should().HaveCount(2,
            "the second burst re-created the address once, and every request of it parked on that one activation");
        Volatile.Read(ref attempts).Should().Be(2, "one initialization per activation, none per request");
    }

    private async Task AssertBurstIsRefused(IMessageHub client, int burst, CancellationToken ct)
    {
        var answers = await Task.WhenAll(Enumerable.Range(0, burst).Select(async _ =>
        {
            try
            {
                var delivery = await Ask(client, ct);
                return $"SERVED {delivery.Message.GetType().Name}";
            }
            catch (DeliveryFailureException ex)
            {
                return $"{ex.Failure!.ErrorType}";
            }
        }));
        Output.WriteLine($"burst answers: {string.Join(", ", answers.GroupBy(a => a).Select(g => $"{g.Key}×{g.Count()}"))}");
        answers.Should().AllBe(nameof(ErrorType.Failed),
            "every request of the burst parked behind the stuck activation and was answered terminally when it retired");
    }
}
