using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Reactive;
using System.Reactive.Linq;
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

    /// <summary>The first initialization faults transiently; every later one succeeds.</summary>
    private IObservable<T> FirstAttemptFaults<T>(T value)
        => Observable.Defer(() => Interlocked.Increment(ref attempts) == 1
            ? Observable.Throw<T>(new TransientConnectionException())
            : Observable.Return(value));

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
        var first = GetHost();
        var client = GetClient();

        // 1. The activation whose init met the fault refuses the requester TRANSIENTLY, in the
        //    owner's own vocabulary — not with the terminal "initialization failed".
        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => client
            .Observe(new ProbeRequest(), o => o.WithTarget(first.Address))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct));
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
        var host = GetHost();
        var client = GetClient();

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct));
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
