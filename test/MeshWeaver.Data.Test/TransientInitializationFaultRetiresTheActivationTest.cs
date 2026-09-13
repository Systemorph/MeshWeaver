using System;
using System.Collections.Generic;
using System.Data.Common;
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
/// Systemorph/MeshWeaver#4067 / #4068 — an initialization that meets a TRANSIENT infrastructure
/// fault (the database unreachable, a name unresolved) must not latch the activation FAILED.
///
/// <para>Both seams are pinned: the <c>DataContext</c> gate (a data source's initial load faults —
/// the #4068 shape) and the hub's own BuildupActions (the #4067 shape). In each, the FIRST
/// activation's initialization throws a transient <see cref="DbException"/> and the SECOND
/// succeeds — the dependency "came back" — so the contract can be read directly: the first
/// requester is refused <see cref="ErrorType.ShuttingDown"/> ("ask again", the owner's own
/// vocabulary), that activation goes to <see cref="MessageHubRunLevel.Dead"/>, and the very next
/// delivery reactivates the address and is SERVED. A CONTROL without
/// <see cref="MessageHubConfiguration.WithReactivationOnDemand"/> keeps today's latch
/// (<see cref="ErrorType.Failed"/>, terminal), which is what proves the marker gates it.</para>
/// </summary>
public class TransientInitializationFaultRetiresTheActivationTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>The shape Npgsql hands back for "Failed to connect" / "Name or service not known":
    /// an ADO.NET exception whose own <see cref="DbException.IsTransient"/> says so.</summary>
    private sealed class TransientConnectionException()
        : DbException("Failed to connect to 10.42.18.4:5432 (Name or service not known)")
    {
        public override bool IsTransient => true;
    }

    private record Item(string Id);

    private record ProbeRequest : IRequest<ProbeResponse>;

    private record ProbeResponse;

    public enum Seam
    {
        DataContext,
        BuildupAction,
    }

    private int attempts;

    /// <summary>Signalled by the FIRST initialization attempt once it is parked — the hub exists,
    /// its gates are closed, and it has not faulted yet. That instant is the test's fence.</summary>
    private readonly AsyncSubject<Unit> firstAttemptParked = new();

    /// <summary>Opened by the test to let the parked first attempt fault.</summary>
    private readonly AsyncSubject<Unit> releaseFault = new();

    /// <summary>
    /// The first initialization attempt PARKS until the test releases it and then faults
    /// transiently; every later attempt succeeds at once.
    ///
    /// <para>🚨 The park is the fix for a race this test had and CI found (queue build
    /// 34750238233, shard 1, the <c>BuildupAction</c> seam): the fault fired 2 ms after the hub was
    /// built — before the test's request had even been posted — so the request activated the FRESH
    /// hub and was SERVED. That is the CORRECT behaviour for a request arriving after the
    /// retirement (step 3 asserts exactly it), but the assertion was written for the OTHER
    /// ordering, so it read "No exception was thrown". Whether the request is in flight when the
    /// activation retires was decided by how long <c>GetClient()</c> took — a coin toss, and a
    /// test that asserts one side of a coin toss is a flake with an opinion. Parking the attempt
    /// makes the ordering a FACT rather than a hope.</para>
    /// </summary>
    private IObservable<T> FirstAttemptFaults<T>(T value)
        => Observable.Defer(() =>
        {
            if (Interlocked.Increment(ref attempts) != 1)
                return Observable.Return(value);
            firstAttemptParked.OnNext(Unit.Default);
            firstAttemptParked.OnCompleted();
            return releaseFault.SelectMany(_ => Observable.Throw<T>(new TransientConnectionException()));
        });

    /// <summary>
    /// True when <paramref name="trail"/> shows the request IN FLIGHT AT <paramref name="address"/>
    /// — that hub has taken it into a queue of its own, so it is owed an answer by THIS activation.
    ///
    /// <para>🚨 The two seams park it in DIFFERENT queues, and a fence that knew only one of them
    /// is how this test wasted a CI run. A <c>DataContext</c> initialises off the turn, so the
    /// hub's pump is free: the delivery is dequeued, found on-target behind the closed
    /// <c>DataContextInit</c> gate, and lands in the DEFERRED queue
    /// (<c>DEFERRED gates=[…]</c>). A <c>BuildupAction</c> runs ON the init turn, so the pump is
    /// BUSY: the delivery sits in the MAIN queue and never reaches the deferral decision —
    /// measured, with the report this branch's sibling shipped:
    /// <c>target pump now: host/1 RunLevel=Starting turn=InitializeHubRequest running 35004ms
    /// buffer=1 deferred=0</c>. Either queue is "in flight at this activation"; the wait accepts
    /// both and requires the hub, because several hubs appear on one trail and the stage renders
    /// as <c>{stage}@{hub}(+Nms)</c>.</para>
    /// </summary>
    private static bool InFlightAt(string trail, Address address)
    {
        foreach (var token in (string[])["QUEUED queue=", "DEFERRED gates="])
            for (var i = trail.IndexOf(token, StringComparison.Ordinal); i >= 0;
                 i = trail.IndexOf(token, i + token.Length, StringComparison.Ordinal))
            {
                var end = trail.IndexOf(" → ", i, StringComparison.Ordinal);
                var stage = end < 0 ? trail[i..] : trail[i..end];
                if (stage.Contains($"@{address}(", StringComparison.Ordinal))
                    return true;
            }
        return false;
    }

    private MessageHubConfiguration HostFor(MessageHubConfiguration c, Seam seam, bool reactivatesOnDemand)
    {
        c = c
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(ProbeRequest), typeof(ProbeResponse))
            .WithHandler<ProbeRequest>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            });
        if (reactivatesOnDemand)
            c = c.WithReactivationOnDemand();
        return seam == Seam.DataContext
            ? c.AddData(data => data.AddSource(src => src.WithType<Item>(t => t
                .WithKey(i => i.Id)
                .WithInitialData(() => FirstAttemptFaults<IEnumerable<Item>>([new Item("one")])))))
            : c.WithInitialization(_ => FirstAttemptFaults(Unit.Default));
    }

    private Seam seam;
    private bool reactivatesOnDemand = true;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => HostFor(configuration, seam, reactivatesOnDemand);

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithTypes(typeof(ProbeRequest), typeof(ProbeResponse));

    [Theory(Timeout = 120_000)]
    [InlineData(Seam.DataContext)]
    [InlineData(Seam.BuildupAction)]
    public async Task ATransientFault_RetiresTheActivation_AndTheNextDeliveryIsServedByAFreshOne(Seam which)
    {
        seam = which;
        var ct = TestContext.Current.CancellationToken;
        // The client FIRST: its own hub construction must not sit inside the window this test
        // fences, and nothing about it is under test.
        var client = GetClient();
        var first = GetHost();

        // THE FENCE, in two steps, because the assertion below is about ONE ordering and both
        // orderings are legal: (a) the first initialization attempt is parked — the activation
        // exists and has not faulted; (b) the request is in flight AT that activation (in its main
        // queue behind the init turn, or in its deferred queue behind the init gate — see
        // InFlightAt). Only then is the fault released, so "the request was in flight when the
        // activation retired" is a fact of the run rather than a race against GetClient().
        await firstAttemptParked.Should().Within(TestTimeouts.Convergence).Emit(
            "the first initialization attempt must be parked before the request is posted");

        var requestId = Guid.NewGuid().ToString("N");
        var response = client
            .Observe((object)new ProbeRequest(), o => o.WithTarget(first.Address), requestId)!
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Mesh.DescribeRequestFate(requestId))
            .Where(trail => InFlightAt(trail, first.Address))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"[fence] in flight at the first activation: {Mesh.DescribeRequestFate(requestId)}");

        releaseFault.OnNext(Unit.Default);
        releaseFault.OnCompleted();

        // 1. The activation whose init met the fault refuses the requester TRANSIENTLY, in the
        //    owner's own vocabulary — not with the terminal "initialization failed".
        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        Output.WriteLine($"first activation refused: errorType={failure.Failure!.ErrorType} message={failure.Failure.Message}");
        failure.Failure.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "a transient infrastructure fault is not a property of the activation — the requester "
            + "must be told to ask again, never that the address is broken");
        failure.Failure.Message.Should().Contain("transient infrastructure fault",
            "the refusal names the cause the caller should expect to clear");
        ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, first.Address).Should().BeTrue();

        // 2. That activation is GONE — not parked FAILED with an InitializationError.
        await first.DisposalCompleted.FirstOrDefaultAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        first.RunLevel.Should().Be(MessageHubRunLevel.Dead);
        ((MessageHub)first).InitializationError.Should().BeNull(
            "a retired activation records no FAILED marker — the marker describes one that stays");

        // 3. The next delivery to the same address activates a FRESH hub, whose initialization
        //    runs again — and now succeeds, because the dependency is back.
        var served = await client
            .Observe(new ProbeRequest(), o => o.WithTarget(first.Address))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        served.Message.Should().BeOfType<ProbeResponse>(
            "the reactivated address serves — this is what the FAILED latch made impossible");
        var second = GetHost();
        second.Should().NotBeSameAs(first, "the address reactivated on a new hub instance");
        Volatile.Read(ref attempts).Should().Be(2,
            "the initialization ran once per activation: the faulting one and the succeeding one");
    }

    /// <summary>
    /// The CONTROL: without <see cref="MessageHubConfiguration.WithReactivationOnDemand"/> the same
    /// transient fault keeps today's latch — the hub stays, marked FAILED, and answers terminally.
    /// A hub nobody re-creates on demand would not come back if retired, so the latch is the honest
    /// answer for it; this pins that the marker, not the fault, is what selects the retirement.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(Seam.DataContext)]
    [InlineData(Seam.BuildupAction)]
    public async Task WithoutReactivationOnDemand_TheSameFaultKeepsTheFailedLatch(Seam which)
    {
        seam = which;
        reactivatesOnDemand = false;
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();
        var host = GetHost();

        // The same fence as the positive case, for the same reason: the control must compare the
        // two paths on the SAME ordering, or it is comparing two different experiments.
        await firstAttemptParked.Should().Within(TestTimeouts.Convergence).Emit(
            "the first initialization attempt must be parked before the request is posted");

        var requestId = Guid.NewGuid().ToString("N");
        var response = client
            .Observe((object)new ProbeRequest(), o => o.WithTarget(host.Address), requestId)!
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Mesh.DescribeRequestFate(requestId))
            .Where(trail => InFlightAt(trail, host.Address))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        releaseFault.OnNext(Unit.Default);
        releaseFault.OnCompleted();

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        Output.WriteLine($"latched: errorType={failure.Failure!.ErrorType} message={failure.Failure.Message}");
        failure.Failure.ErrorType.Should().Be(ErrorType.Failed);
        failure.Failure.Message.Should().Contain("initialization failed");
        host.RunLevel.Should().Be(MessageHubRunLevel.Started, "a latched hub stays, refusing");
        var latched = which == Seam.DataContext
            ? host.GetWorkspace().DataContext.InitializationError
            : ((MessageHub)host).InitializationError;
        latched.Should().NotBeNull("the FAILED marker is recorded on the hub that stays");
        Volatile.Read(ref attempts).Should().Be(1, "nothing re-ran the initialization");
    }
}
