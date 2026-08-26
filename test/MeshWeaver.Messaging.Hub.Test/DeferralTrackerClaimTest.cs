using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Issue #2176 — <c>MessageService</c> retired a parked deferral's <see cref="CancellationTokenSource"/>
/// WITHOUT claiming it, so two retirement paths could cancel and dispose the same source and the
/// second one threw <c>ObjectDisposedException: The CancellationTokenSource has been disposed.</c>
///
/// <para>In production it surfaced out of <c>MessageService.Dispose()</c> itself and was logged by
/// the hub as <c>Error during shutdown of hub Store/Catalog …</c> (memex-cloud, 2026-08-24) — a
/// <c>fail</c>-level line on a shutdown that actually completed, i.e. exactly the kind of noise that
/// pages on-call for nothing while hiding the real defect: three of the five paths that retire a
/// deferral iterated <c>deferredDeliveries</c> and cancelled + disposed each tracker IN PLACE,
/// removing nothing until a trailing <c>Clear()</c>. The other two (the drain in
/// <c>ProcessDeferredMessage</c> and the deferral-timeout continuation, which fires on the
/// ThreadPool) claimed correctly with <c>TryRemove</c> — so an already-disposed tracker stayed
/// visible to whoever came next.</para>
///
/// <para>This test drives two of the unclaimed drains at once through the public surface:
/// <c>FailGate</c> runs its backlog drain OUTSIDE the gate-state lock, so two gates failing
/// concurrently is a supported, ordinary interleave. Pre-fix the second cancel throws straight out
/// of <c>FailGate</c> into the caller.</para>
/// </summary>
public class DeferralTrackerClaimTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string GateA = "never-opens-a";
    private const string GateB = "never-opens-b";

    /// <summary>How deep the parked backlog is. Wide enough that two concurrent drains overlap on
    /// it; well under <c>MaxDeferredMessages</c> (512) so nothing is dropped as overflow.</summary>
    private const int Parked = 48;

    private record ParkRequest(int Index) : IRequest<ParkResponse>;
    private record ParkResponse;

    /// <summary>Let through both gates, so a completed probe proves — FIFO on the hub's single turn
    /// loop — that everything posted before it is already parked.</summary>
    private record ProbeRequest : IRequest<ProbeResponse>;
    private record ProbeResponse;

    /// <summary>Registers the probe/park types so the wire carries explicit discriminators.</summary>
    private static MessageHubConfiguration WithTestTypes(MessageHubConfiguration configuration)
        => configuration
            .WithType<ParkRequest>(nameof(ParkRequest))
            .WithType<ParkResponse>(nameof(ParkResponse))
            .WithType<ProbeRequest>(nameof(ProbeRequest))
            .WithType<ProbeResponse>(nameof(ProbeResponse));

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => WithTestTypes(base.ConfigureMesh(conf));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => WithTestTypes(base.ConfigureClient(configuration));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => WithTestTypes(configuration)
            .WithHandler<ParkRequest>((h, request) =>
            {
                h.Post(new ParkResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .WithHandler<ProbeRequest>((h, request) =>
            {
                h.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            // Two gates that never open on their own — every ParkRequest parks behind BOTH.
            .WithInitializationGate(GateA, d => d.Message is ProbeRequest)
            .WithInitializationGate(GateB, d => d.Message is ProbeRequest);

    [Fact(Timeout = 60000)]
    public async Task Two_concurrent_backlog_drains_never_double_dispose_a_deferral_tracker()
    {
        var host = GetHost();
        var client = GetClient();

        // Park a backlog. Each request is answered by whichever drain claims it, so the tasks also
        // prove nothing is silently abandoned (the trailing Clear() used to drop late arrivals).
        var parked = Enumerable.Range(0, Parked)
            .Select(i => (Task)client
                .Observe(new ParkRequest(i), o => o.WithTarget(host.Address))
                .FirstAsync()
                .ToTask())
            .ToArray();

        // The probe passes both gate predicates, so it runs on the host's turn loop only after every
        // ParkRequest posted before it has been deferred. No sleep, no poll.
        await client.Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(30.Seconds()).ToTask(TestContext.Current.CancellationToken);

        // Both gates go dead at once. FailGate takes gateStateLock only to mark the gate; the
        // backlog drain runs outside it, so these two drains genuinely overlap.
        using var start = new Barrier(2);
        var a = Task.Run(() => { start.SignalAndWait(); return Record.Exception(() => host.FailGate(GateA, "dead a")); });
        var b = Task.Run(() => { start.SignalAndWait(); return Record.Exception(() => host.FailGate(GateB, "dead b")); });
        var faults = await Task.WhenAll(a, b);

        foreach (var fault in faults)
            fault.Should().NotBeOfType<ObjectDisposedException>(
                "a deferral tracker must be CLAIMED (TryRemove) before it is cancelled and disposed — "
                + "two drains cancelling the same CancellationTokenSource is the shutdown-time "
                + "ObjectDisposedException of issue #2176");

        // …and every parked delivery still got its terminal answer. A drain that claims must not
        // become a drain that drops: the deliveries are answered, not silently abandoned.
        var answered = await Task.WhenAll(parked.Select(WaitForTerminal));
        answered.Should().AllSatisfy(t => t.Should().BeTrue(
            "every delivery parked behind a dead gate must be answered, never abandoned"));
    }

    private static async Task<bool> WaitForTerminal(Task pending)
    {
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            // A DeliveryFailure surfaces as an exception on the awaiter — that IS the terminal answer.
            return true;
        }
    }
}
