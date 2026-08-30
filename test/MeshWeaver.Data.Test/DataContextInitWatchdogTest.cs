using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the TIMED-OUT outcome of the <see cref="DataContext"/> init watchdog — the branch the
/// 2026-06-26 prod wedge bought (a hung init left the gate closed and every message deferred →
/// NACKed → resubscribed, a path-resolution storm that GC-thrashed the portal).
///
/// <para>Together with its three siblings this fixes the watchdog's four terminal outcomes in
/// place across the #2528 rewrite (the Task.WhenAny + Task.Delay + ContinueWith timer race became
/// an Amb over init-settled / time-box / disarm — a subscription, per #2488):</para>
/// <list type="bullet">
/// <item><b>timed out</b> — THIS test: fail-level diagnostic, <see cref="DataContext.InitializationError"/>
/// = <see cref="TimeoutException"/>, rejection handler answers fast, gate OPEN;</item>
/// <item><b>faulted</b> — <see cref="DataContextInitFaultedTest"/>;</item>
/// <item><b>shutdown (disarm)</b> — <c>DataContextDisposeDuringInitTest</c> (#1122: no post-mortem
/// FAILED residue) and <c>DataContextShutdownGateAnswersDeferredTest</c> (#1270: FailGate still
/// ANSWERS what parked behind the gate);</item>
/// <item><b>clean</b> — every AddData round-trip test in this project (e.g.
/// <c>DataContextIntegrationTest</c>): data flows ⇔ the gate opened cleanly.</item>
/// </list>
///
/// <para><b>What a red here means.</b> A <see cref="TimeoutException"/> from the Observe below
/// means the time-box never fired or the gate never got its answer — the exact silent-proceed /
/// gate-shut-forever failure modes the reactive rewrite must not introduce.</para>
/// </summary>
public class DataContextInitTimeoutTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record HangingItem(string Id);

    // NOT PingRequest: the DataContextInit gate deliberately exempts liveness pings
    // (DataExtensions.WithInitializationGate), so only a real request parks behind it.
    private record ProbeRequest : IRequest<ProbeResponse>;

    private record ProbeResponse;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(ProbeRequest), typeof(ProbeResponse))
            // The handler WOULD answer, so a pass can only come from the FAILED-state
            // rejection — never from the request quietly being served after all.
            .WithHandler<ProbeRequest>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .AddData(data => data
                // Short bound so the terminal FAILED state is observed fast; prod is 120s.
                .WithInitializationTimeout(TimeSpan.FromMilliseconds(1500))
                .AddSource(src => src.WithType<HangingItem>(t => t
                    .WithKey(i => i.Id)
                    // An initial load that NEVER completes — the "stuck NodeType compile / data
                    // source that never initialised" the time-box exists for.
                    .WithInitialData(() => Observable.Never<IEnumerable<HangingItem>>()))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithTypes(typeof(ProbeRequest), typeof(ProbeResponse));

    [Fact(Timeout = 60000)]
    public async Task HungInit_TripsTheTimeBox_FailsFastAndLoud_NeverSilentlyProceeds()
    {
        var host = GetHost();
        var client = GetClient();

        // The request parks behind the DataContextInit gate; the time-box fires at ~1.5s, the
        // rejection handler answers it with a typed DeliveryFailure. 15s budget: far above the
        // bound (so the graceful failure is observed), far below the 30s deferral timeout (so a
        // gate left shut surfaces as this test's own TimeoutException and fails it).
        var act = () => client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(15)).Await();

        var ex = (await act.Should().ThrowAsync<Exception>(
            "a hub whose data-source init hung must answer requests with an error, not serve "
            + "as if initialized and not hang")).Which;

        ex.Should().NotBeOfType<TimeoutException>(
            "the time-box must fail the hub FAST and terminally — this test timing out means "
            + "the gate never got its answer, the wedge the time-box exists to prevent");
        ex.ToString().Should().Contain("initialization failed",
            "the rejection must say WHY the hub refuses, not read as a generic delivery error");
        ex.ToString().Should().Contain("did not complete within",
            "the timeout diagnostic must surface the time-box expiry, never silently proceed");

        // The FAILED state is recorded where diagnostics (and the MeshNodeStreamCache negative
        // cache) read it — a TimeoutException naming the budget and the likely causes.
        var initError = host.GetWorkspace().DataContext.InitializationError;
        initError.Should().BeOfType<TimeoutException>(
            "a hung init reaches the SAME terminal failed state as a thrown one, via a "
            + "TimeoutException that names the budget");
    }
}

/// <summary>
/// Pins the FAULTED outcome of the <see cref="DataContext"/> init watchdog: a data source whose
/// initial load THROWS must drive the hub to the terminal FAILED state — error recorded, rejection
/// handler answering fast, gate open — and the watchdog's init-settled arm must run the settle
/// body for a faulted init exactly as for a clean one (the arm is Materialize'd; an OnError that
/// skipped past the body to an error arm would leave the gate shut forever).
/// See <see cref="DataContextInitTimeoutTest"/> for the outcome map.
/// </summary>
public class DataContextInitFaultedTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string InitFaultMarker = "boom during initial data load";

    private record FaultyItem(string Id);

    // NOT PingRequest — the gate exempts liveness pings; see DataContextInitTimeoutTest.
    private record ProbeRequest : IRequest<ProbeResponse>;

    private record ProbeResponse;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(ProbeRequest), typeof(ProbeResponse))
            .WithHandler<ProbeRequest>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .AddData(data => data
                .AddSource(src => src.WithType<FaultyItem>(t => t
                    .WithKey(i => i.Id)
                    // An initial load that FAULTS — the wedges-to-zero branch (fail fast →
                    // reject subsequent requests) the watchdog has carried since before the
                    // time-box.
                    .WithInitialData(() =>
                        Observable.Throw<IEnumerable<FaultyItem>>(
                            new InvalidOperationException(InitFaultMarker))))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithTypes(typeof(ProbeRequest), typeof(ProbeResponse));

    [Fact(Timeout = 60000)]
    public async Task FaultedInit_ReachesTerminalFailedState_AndAnswersFast()
    {
        var host = GetHost();
        var client = GetClient();

        var act = () => client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(15)).Await();

        var ex = (await act.Should().ThrowAsync<Exception>(
            "a hub whose data-source init threw must answer requests with an error")).Which;

        ex.Should().NotBeOfType<TimeoutException>(
            "a faulted init must be answered FAST by the rejection handler — a timeout means "
            + "the faulted arm skipped the settle body and left the gate shut");
        ex.ToString().Should().Contain("initialization failed",
            "the rejection must be reported as an initialization failure");

        var initError = host.GetWorkspace().DataContext.InitializationError;
        initError.Should().NotBeNull(
            "the hub must expose the FAILED status marker after a faulted init");
        initError!.ToString().Should().Contain(InitFaultMarker,
            "the recorded failure must carry the SPECIFIC init fault for diagnosis");
    }
}
