using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// The hub's own request budget — the latency a request that FELL THROUGH the faulted arm
    /// would wait out. Derived from the mechanism, not from a feeling of "fast": the rejection
    /// handler answers in milliseconds, so any headroom below this separates the two outcomes
    /// while leaving a loaded runner far more room than it needs.
    /// </summary>
    private static readonly TimeSpan HubRequestBudget = TimeSpan.FromSeconds(30);

    [Fact(Timeout = 60000)]
    public async Task FaultedInit_ReachesTerminalFailedState_AndAnswersFast()
    {
        var host = GetHost();
        var client = GetClient();

        // 🚨 ONE bound was doing TWO jobs, and they want opposite values (#2700).
        //
        // This test asserts both "a faulted init is ANSWERED at all" (liveness — wants a generous
        // bound, so a timeout means NEVER) and "…AndAnswersFast" (latency — wants a tight one).
        // A single 15 s wait conflated them: a loaded runner pushed the rejection past it, the
        // wait produced a TimeoutException, and the assertion read that as "the gate is shut" —
        // the defect's own signature, for a defect that was not there. It ejected core #2803 from
        // the merge queue at 17:32Z (a PR touching only Mesh.Contract/Security and an operator
        // script, which cannot reach a DataContext). Simply widening to 60 s would have deleted
        // the latency half instead — the test would still pass if the answer took 55 s, which is
        // precisely what its name says it guards.
        //
        // So they are separated. The WAIT is generous, and the ELAPSED time is asserted on its
        // own against a threshold derived from the mechanism rather than from a feeling of
        // "fast": the rejection handler answers in milliseconds, whereas the failure mode — the
        // faulted arm skipping the settle body — leaves the request to fall through to the hub's
        // own request budget. Anything at or beyond that budget IS the fall-through.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var act = () => client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await();

        var ex = (await act.Should().ThrowAsync<Exception>(
            "a hub whose data-source init threw must answer requests with an error")).Which;
        stopwatch.Stop();

        ex.Should().NotBeOfType<TimeoutException>(
            "a faulted init must be ANSWERED by the rejection handler — a timeout at a 60 s bound "
            + "means the faulted arm skipped the settle body and left the gate shut for good");

        stopwatch.Elapsed.Should().BeLessThan(HubRequestBudget,
            $"the rejection handler answers in milliseconds; this took {stopwatch.Elapsed.TotalSeconds:F1}s, "
            + $"at or beyond the hub's own {HubRequestBudget.TotalSeconds:F0}s request budget — which is what a "
            + "request that FELL THROUGH the faulted arm looks like, rather than one it rejected");
        ex.ToString().Should().Contain("initialization failed",
            "the rejection must be reported as an initialization failure");

        var initError = host.GetWorkspace().DataContext.InitializationError;
        initError.Should().NotBeNull(
            "the hub must expose the FAILED status marker after a faulted init");
        initError!.ToString().Should().Contain(InitFaultMarker,
            "the recorded failure must carry the SPECIFIC init fault for diagnosis");
    }
}

/// <summary>
/// The DETERMINISTIC repro of the #2625 flake, and the regression guard for its fix.
///
/// <para><b>The race.</b> <c>SynchronizationStream</c>'s constructor asks
/// <c>Host.GetHostedHub(sync/…, HostedHubCreation.Always)</c> for its sub-hub, and
/// <c>MessageHubConfiguration.Build</c> STARTS that hub — it posts <c>InitializeHubRequest</c> —
/// before <c>GetHostedHub</c> returns. So the sub-hub's BuildupActions can run on the action
/// block WHILE the constructor is still executing, i.e. before the constructor has assigned the
/// stream's <c>Hub</c> field. A data source whose initial load faults SYNCHRONOUSLY
/// (<c>Observable.Throw</c>) does exactly that in ~zero time.</para>
///
/// <para><b>What that used to cost.</b> The fault reached
/// <c>SynchronizationStream.OnError</c>, whose <c>if (Hub is not null)</c> guard then SKIPPED
/// both <c>Hub.FailStartup(error)</c> and <c>Hub.OpenGate(SynchronizationGate)</c> — silently.
/// The sync hub therefore never reached <c>Started</c> and its <c>Started</c> task never
/// settled, so <c>IDataSource.Initialized</c> (a <c>WhenAll</c> over those tasks) hung, so
/// <see cref="DataContext"/>'s gate was never settled and every request to the owning hub sat
/// behind <c>DataContextInit</c> until an unrelated deadline expired. The hub logged
/// "initialization failed … FAILED state" in 4 ms and answered nothing for the rest of the
/// test's budget — the exact signature in the issue.</para>
///
/// <para><b>Why this test is deterministic where a 25-run loop was not.</b>
/// <c>HostedHubsCollection</c> publishes <c>HubAdded</c> on the CONSTRUCTING thread, inside the
/// creation <c>Lazy</c> — after <c>Build</c> has posted the init request and before
/// <c>GetHostedHub</c> returns to the stream's constructor. Parking that thread there until the
/// sub-hub has recorded its init failure pins the CI interleaving with no timing luck at all.
/// The park is the SUBJECT of the test, so it is a bounded <c>SpinWait.SpinUntil</c> over a
/// value another worker writes (AGENTS.md's sanctioned shape) — not a gate.</para>
/// </summary>
public class DataContextFaultedInitBeforeStreamHubBoundTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string InitFaultMarker = "boom before the stream bound its hub";

    private record FaultyItem(string Id);

    // NOT PingRequest — the gate exempts liveness pings; see DataContextInitTimeoutTest.
    private record ProbeRequest : IRequest<ProbeResponse>;

    private record ProbeResponse;

    /// <summary>
    /// Set once, so only the FIRST hosted sub-hub — the data source's own
    /// <c>sync/…</c> stream hub — is parked; later ones (reduced streams) run free.
    /// </summary>
    private int parkClaimed;

    /// <summary>True once the park has actually been taken, so the test can assert it staged.</summary>
    private volatile bool parkStaged;

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
            // 🚨 The SYNCHRONOUS WithInitialization overload on purpose: SyncBuildupActions run
            // inside Build(), BEFORE the host starts message processing — so this subscription is
            // guaranteed to be in place before the init turn creates any data-source stream. The
            // observable overload would run on the init turn itself and could miss the emission.
            .WithInitialization(hub => hub.RegisterForDisposal(
                hub.ServiceProvider.GetRequiredService<HostedHubsCollection>()
                    .HubAdded
                    .Subscribe(ParkConstructionUntilChildInitFailed)))
            .AddData(data => data
                .AddSource(src => src.WithType<FaultyItem>(t => t
                    .WithKey(i => i.Id)
                    .WithInitialData(() =>
                        Observable.Throw<IEnumerable<FaultyItem>>(
                            new InvalidOperationException(InitFaultMarker))))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithTypes(typeof(ProbeRequest), typeof(ProbeResponse));

    /// <summary>What the park actually observed — written to test output so a future red says
    /// whether the window was staged at all, not merely that the probe was slow.</summary>
    private string parkDiagnostics = "(never parked)";

    /// <summary>
    /// The address of the first <c>sync/…</c> sub-hub seen — the data source's own stream hub.
    /// Written and read only on the constructing thread.
    /// </summary>
    private Address? firstSyncAddress;

    private void ParkConstructionUntilChildInitFailed(IMessageHub child)
    {
        if (child is not MessageHub concrete
            || concrete.Address.Type != MeshWeaver.Data.SynchronizationAddress.AddressType)
            return;

        // 🚨 HubAdded fires TWICE per hosted hub, and only the SECOND one is the window.
        //   1. HostedHubsCollection.Add — called from MessageHubConfiguration.Build BEFORE
        //      SyncBuildupActions and BEFORE StartMessageProcessing, so the hub has not yet
        //      been handed its InitializeHubRequest and nothing can fault.
        //   2. HostedHubsCollection.GetHub's creation Lazy — called AFTER Build returned, i.e.
        //      after StartMessageProcessing posted InitializeHubRequest, and BEFORE
        //      GetHostedHub returns to SynchronizationStream's constructor. That is exactly
        //      the interval in which the constructor has NOT yet assigned its Hub field.
        if (firstSyncAddress is null)
        {
            firstSyncAddress = concrete.Address;
            return;
        }
        if (!firstSyncAddress.Equals(concrete.Address))
            return;
        if (Interlocked.Exchange(ref parkClaimed, 1) != 0)
            return;

        parkStaged = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Bounded, and the bound is a DIAGNOSTIC rather than a budget: the sub-hub's init
            // faults synchronously on its own turn scheduler, so this releases in milliseconds.
            // Written in the shape AGENTS.md sanctions for a park that IS the subject of the
            // test — a bounded SpinUntil over a value another worker writes, never a gate.
            SpinWait.SpinUntil(() => concrete.InitializationError is not null, TimeSpan.FromSeconds(10));
        }
        finally
        {
            parkDiagnostics =
                $"parked on {concrete.Address} for {sw.ElapsedMilliseconds}ms, "
                + $"RunLevel={concrete.RunLevel}, InitializationError={concrete.InitializationError?.Message ?? "(null)"}";
        }
    }

    [Fact(Timeout = 60000)]
    public async Task StreamFaultingBeforeItsHubIsBound_StillFailsTheDataContextGateFast()
    {
        var host = GetHost();
        var client = GetClient();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // Generous WAIT / asserted ELAPSED, exactly as DataContextInitFaultedTest separates them
        // (#2700): the wait must be long enough that a timeout means NEVER, the elapsed assertion
        // is what carries "…Fast".
        var act = () => client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).Await();

        var ex = (await act.Should().ThrowAsync<Exception>(
            "a hub whose data-source init threw must answer requests with an error")).Which;
        stopwatch.Stop();

        Output.WriteLine($"PARK: {parkDiagnostics}");
        parkStaged.Should().BeTrue(
            "the repro only means anything if the stream's construction was actually parked at "
            + "HubAdded — a HubAdded that never fired would make this test a tautology");

        ex.Should().NotBeOfType<TimeoutException>(
            "a faulted init must be ANSWERED by the rejection handler even when the sub-hub's init "
            + "turn beat the stream's constructor to the Hub assignment — a timeout here is the "
            + "#2625 wedge: OnError skipped FailStartup, so Started never settled and the "
            + "DataContextInit gate was never given its answer");

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25),
            $"the rejection handler answers in milliseconds; this took {stopwatch.Elapsed.TotalSeconds:F1}s, "
            + "which is a request that fell through to a deferral/time-box deadline rather than one "
            + "the faulted arm rejected");

        ex.ToString().Should().Contain("initialization failed",
            "the rejection must be reported as an initialization failure");

        var initError = host.GetWorkspace().DataContext.InitializationError;
        initError.Should().NotBeNull(
            "the DataContext must reach its terminal FAILED state, not sit un-settled");
        initError!.ToString().Should().Contain(InitFaultMarker,
            "the recorded failure must carry the SPECIFIC init fault — a TimeoutException here "
            + "would mean the time-box settled the gate instead of the fault");
    }
}
