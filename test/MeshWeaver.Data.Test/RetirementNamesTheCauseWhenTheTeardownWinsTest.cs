using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Systemorph/MeshWeaver#4261 — the DETERMINISTIC pin for the race
/// <see cref="TransientInitializationFaultRetiresTheActivationTest"/> hit at 3 runs in 36.
///
/// <para><b>The race.</b> <c>DataContext.SettleInitializationGate</c> retires an activation whose
/// initialization met a transient infrastructure fault. It used to call <c>Hub.Dispose()</c> FIRST
/// and <c>Hub.FailGate(cause)</c> SECOND, and the comment beside it stated that ordering as a
/// requirement — the drain classified the refusal by reading <c>hub.IsShuttingDown</c> at drain
/// time, so the teardown had to be under way for the answer to come out transient. The code could
/// not enforce it: the settle runs on the THREAD POOL (<c>OpenInitializationGate</c> ends with
/// <c>ObserveOn(TaskPoolScheduler.Default)</c>), while <c>Dispose()</c> merely POSTS a
/// <c>ShutdownRequest</c> whose turn reaches <c>MessageService.Dispose</c> later, on the hub's
/// action block. Both ends drain the SAME deferred backlog, so whichever arrived first decided
/// what the requester was told — measured in the field 1 ms apart, the requester reading "the
/// message was never processed" instead of the database fault that retired the activation.</para>
///
/// <para><b>Why the outcome assertion alone cannot pin it, and what does.</b> Asserting the
/// MESSAGE only fails when the disposal drain happens to win, which is the coin toss this issue is
/// about. The state that is a FACT of every run is read one frame earlier: at the instant the hub
/// enters its teardown (<see cref="IMessageHub.ShuttingDown"/> fires synchronously inside
/// <c>Dispose()</c>, on the settle thread), the deferred backlog must ALREADY be empty — because
/// the gate was failed before <c>Dispose()</c> was called at all. On the unfixed ordering the
/// backlog is still parked at that instant, every time, which is precisely what leaves it for the
/// teardown to answer. Verified as an A/B: with only <c>SettleInitializationGate</c>'s two
/// statements swapped back, this reads <c>deferred=1</c> and fails.</para>
///
/// <para>That is the mechanism-level statement of the fix — "the backlog is answered before a
/// teardown exists that could answer it differently" — rather than a re-run of the outcome the
/// race sometimes produced.</para>
///
/// <para><b>The test's own ordering (#4285).</b> The fix this pins is exactly what makes the
/// refusal arrive BEFORE the teardown: <c>FailGate</c> answers the client, and <c>Dispose()</c>
/// is the next statement on the settle thread. So the client can observe its refusal — and this
/// test can reach its assertion — while that statement has not run yet. The snapshot is taken at
/// the first instant of the teardown, and the test WAITS for it (bounded) rather than assuming
/// it already exists when the refusal is read; measured as "Expected value not to be &lt;null&gt;"
/// at 2 in a handful of runs before the wait, on diffs that could not reach this code.</para>
/// </summary>
public class RetirementNamesTheCauseWhenTheTeardownWinsTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>The shape Npgsql hands back for "Failed to connect" / "Name or service not known".</summary>
    private sealed class TransientConnectionException()
        : DbException("Failed to connect to 10.42.18.4:5432 (Name or service not known)")
    {
        public override bool IsTransient => true;
    }

    private record Item(string Id);

    private record ProbeRequest : IRequest<ProbeResponse>;

    private record ProbeResponse;

    private int attempts;

    /// <summary>Signalled once the first initialization attempt is parked — the activation exists,
    /// its gate is closed, and it has not faulted yet.</summary>
    private readonly AsyncSubject<Unit> firstAttemptParked = new();

    /// <summary>Opened by the test to let the parked first attempt fault.</summary>
    private readonly AsyncSubject<Unit> releaseFault = new();

    /// <summary>
    /// The victim's own queue snapshot at the FIRST instant of its teardown, captured on the
    /// settle thread inside <c>Dispose()</c>. Emitted once there; the test awaits it, because the
    /// refusal it is read after is delivered BEFORE the teardown that captures it (#4285).
    /// </summary>
    private readonly AsyncSubject<string> diagnosticsAtTeardownEntry = new();

    private IObservable<IEnumerable<Item>> FirstAttemptFaults()
        => Observable.Defer(() =>
        {
            if (Interlocked.Increment(ref attempts) != 1)
                return Observable.Return<IEnumerable<Item>>([new Item("one")]);
            firstAttemptParked.OnNext(Unit.Default);
            firstAttemptParked.OnCompleted();
            return releaseFault.SelectMany(
                _ => Observable.Throw<IEnumerable<Item>>(new TransientConnectionException()));
        });

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(ProbeRequest), typeof(ProbeResponse))
            .WithReactivationOnDemand()
            .WithHandler<ProbeRequest>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .AddData(data => data.AddSource(src => src.WithType<Item>(t => t
                .WithKey(i => i.Id)
                .WithInitialData(FirstAttemptFaults))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithTypes(typeof(ProbeRequest), typeof(ProbeResponse));

    [Fact(Timeout = 120_000)]
    public async Task TheBacklogIsAnswered_BeforeTheTeardownThatCouldAnswerItDifferently()
    {
        var ct = TestContext.Current.CancellationToken;
        // The client FIRST: its construction must not sit inside the window this test fences.
        var client = GetClient();
        var victim = GetHost();

        // Armed before anything can retire the activation. ShuttingDown fires exactly once, at the
        // first instant of this hub's teardown, synchronously on whichever thread called Dispose().
        using var teardownEntry = victim.ShuttingDown.Subscribe(_ =>
        {
            diagnosticsAtTeardownEntry.OnNext(victim.GetPendingRequestDiagnostics());
            diagnosticsAtTeardownEntry.OnCompleted();
        });

        await firstAttemptParked.Should().Within(TestTimeouts.Convergence).Emit(
            "the first initialization attempt must be parked before the request is posted");

        var requestId = Guid.NewGuid().ToString("N");
        var response = client
            .Observe((object)new ProbeRequest(), o => o.WithTarget(victim.Address), requestId)!
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        // The request must be PARKED behind DataContextInit before the fault is released, or the
        // retirement would have nothing to answer and this test would assert on an empty backlog
        // it never filled.
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Mesh.DescribeRequestFate(requestId))
            .Where(trail => trail.Contains("DEFERRED gates=", StringComparison.Ordinal)
                            && trail.Contains($"@{victim.Address}(", StringComparison.Ordinal))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"[fence] deferred at the victim: {Mesh.DescribeRequestFate(requestId)}");
        Regex.Match(victim.GetPendingRequestDiagnostics(), @"deferred=(\d+)").Groups[1].Value
            .Should().NotBe("0",
                "the positive control: the backlog this test is about is demonstrably parked "
                + "BEFORE the retirement runs, so a later deferred=0 is the retirement's work and "
                + "not an empty queue that was never filled");

        releaseFault.OnNext(Unit.Default);
        releaseFault.OnCompleted();

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        Output.WriteLine($"refused: errorType={failure.Failure!.ErrorType} message={failure.Failure.Message}");

        // The refusal is the gate failure, which precedes the teardown by construction — so the
        // teardown's snapshot is awaited here, not assumed. A teardown that never comes fails
        // this wait by name; without one there is no ordering to assert and the test would be
        // vacuous, which is why this is a bounded wait and not a null check.
        var atEntry = await diagnosticsAtTeardownEntry.Should().Within(TestTimeouts.Convergence).Emit(
            "the retirement disposes the activation right after failing its gate, and the "
            + "snapshot at the first instant of that teardown is what this test measures");
        Output.WriteLine($"[teardown entry] {atEntry}");

        // 🚨 THE DISCRIMINATOR. The retirement answered the backlog before it started the teardown,
        // so nothing was left for the teardown's generic drain to answer.
        Regex.Match(atEntry, @"deferred=(\d+)").Groups[1].Value.Should().Be("0",
            "the gate is failed BEFORE Dispose() is called, so at the first instant of the teardown "
            + "the deferred backlog is already answered with the CAUSE. A non-zero count here is "
            + "the unfixed ordering: the backlog is still parked when the teardown begins, and "
            + "whichever drain reaches it first decides what the requester is told");

        // And the outcome that follows from it — the half a caller actually reads.
        failure.Failure.Message.Should().Contain("transient infrastructure fault",
            "the refusal names the cause the caller should expect to clear");
        failure.Failure.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "a retired activation is transient — 'ask again', never 'this address is broken'");
        ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, victim.Address).Should().BeTrue();
    }
}
