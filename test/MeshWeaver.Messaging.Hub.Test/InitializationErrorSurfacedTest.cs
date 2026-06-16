using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the framework robustness guarantee: when a hub's initialization throws, the hub must
/// <b>fail gracefully</b> — NOT wedge.
///
/// <para><b>Why this exists.</b> A <c>BuildupAction</c> (composed in
/// <see cref="MessageHub"/>'s <c>HandleInitialize</c>) that faulted used to propagate the error
/// out of the <c>Observable.Concat</c>, so the <c>OpenGate(Initialize)</c> step never ran: the
/// Initialize gate stayed closed forever and EVERY subsequent message deferred until the 30s
/// deferral-timeout. To the user that is an unrecoverable hang — the atioz AgenticPension
/// agent-select wedge (2026-06-16): selecting an agent triggered a hub whose init threw, and the
/// whole node went unreachable behind 30s grain timeouts.</para>
///
/// <para><b>The fix</b> wraps init in a big try/catch (<c>HandleInitialize</c>'s <c>.Catch</c>):
/// on a fault the hub records a <see cref="MessageHub.InitializationError"/> ("status failed"),
/// ALWAYS opens the gate so it can still REACT to messages and be torn down, and REFUSES every
/// non-lifecycle request with a typed <see cref="DeliveryFailure"/> (<see cref="ErrorType.Failed"/>)
/// carrying the init error. The error is now observable end-to-end — callers get a
/// <c>DeliveryFailureException</c> and the GUI's area binding renders it instead of spinning.
/// See <c>Doc/Architecture/HubInitializationFailure.md</c>.</para>
///
/// <para><b>RED before the fix:</b> the assertion sees a <see cref="TimeoutException"/> (nothing
/// returns within the 8s budget because the gate never opens). <b>GREEN after:</b> a
/// <c>DeliveryFailureException</c> arrives FAST, carrying "initialization failed: &lt;the error&gt;".</para>
/// </summary>
public class InitializationErrorSurfacedTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string InitErrorMarker = "boom during hub init";

    private record ProbeRequest : IRequest<ProbeResponse>;
    private record ProbeResponse;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            // A normal handler — proves that, absent the init fault, this hub WOULD answer ProbeRequest.
            .WithHandler<ProbeRequest>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            // An async BuildupAction (composed in HandleInitialize) that FAULTS — the exact shape that
            // used to wedge the hub by leaving the Initialize gate closed forever.
            .WithInitialization(_ => Observable.Throw<Unit>(new InvalidOperationException(InitErrorMarker)));

    [Fact(Timeout = 30000)]
    public async Task FaultingBuildupAction_SurfacesTypedInitFailure_FastNotAWedge()
    {
        var host = GetHost();
        var client = GetClient();

        // A FAILED hub answers FAST with a DeliveryFailure (→ DeliveryFailureException via Observe).
        // If the hub wedged (gate never opened), nothing returns within 8s → TimeoutException.
        var act = () => client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(8.Seconds()).ToTask();

        var ex = (await act.Should().ThrowAsync<Exception>(
            "a hub whose init threw must answer requests with an error, not hang")).Which;

        ex.Should().NotBeOfType<TimeoutException>(
            "a FAILED hub must answer FAST with a DeliveryFailure — a TimeoutException means the gate "
            + "never opened and the hub wedged (the regression this test guards against)");
        ex.ToString().Should().Contain("initialization failed",
            "the failure must be reported as an initialization failure");
        ex.ToString().Should().Contain(InitErrorMarker,
            "the FAILED-state rejection must carry the SPECIFIC init error, not a generic deferral timeout");

        // "Status failed": the hub records the init fault while staying alive enough to refuse messages.
        ((MessageHub)host).InitializationError.Should().NotBeNull(
            "the hub must expose a FAILED status marker (InitializationError) after an init fault");
    }
}

/// <summary>
/// Companion for the HANG case — the atioz recurring wedge. A BuildupAction that never emits/completes
/// raises NO exception, so a plain try/catch can't catch it; without a bound the Initialize gate never
/// opens and every message defers to the 30s deferral-timeout (the wedge). <c>HandleInitialize</c> now
/// bounds the buildup with a Timeout (<c>Configuration.StartupTimeout</c>) → "never completes" becomes a
/// TimeoutException the SAME <c>.Catch</c> handles → <see cref="MessageHub.InitializationError"/> set +
/// gate opened + a clear "did not complete within Ns" failure.
///
/// <para>RED before the bound: a request to the hung-init hub never responds within budget →
/// TimeoutException (wedge). GREEN after: a fast DeliveryFailureException, and InitializationError is set
/// (proving the liveness bound → .Catch fired, not merely the startup-timer backlog drain).</para>
/// </summary>
public class InitializationHangSurfacedTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            // Short bound so the test is fast; prod uses HandleInitialize's generous default.
            .WithStartupTimeout(TimeSpan.FromSeconds(2))
            // A BuildupAction that HANGS forever — never emits, never completes, never throws.
            .WithInitialization(_ => Observable.Never<Unit>());

    [Fact(Timeout = 30000)]
    public async Task HangingBuildupAction_TimesOut_FailsGracefully_NotAWedge()
    {
        var host = GetHost();
        var client = GetClient();

        // 15s budget: > the 2s liveness bound (so the graceful failure is observed) but < the 30s
        // deferral-timeout (so a genuine wedge surfaces as a TimeoutException and fails the test).
        var act = () => client
            .Observe(new PingRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(15.Seconds()).ToTask();

        var ex = (await act.Should().ThrowAsync<Exception>(
            "a hub whose init HUNG must answer requests with an error, not hang forever")).Which;

        ex.Should().NotBeOfType<TimeoutException>(
            "a hung init must fail FAST via the liveness bound (gate opens FAILED), not leave every "
            + "message deferring to the 30s deferral-timeout — the wedge this guards against");
        ex.ToString().Should().Contain("initiali", "the failure must be reported as an initialization failure");

        // Proves the LIVENESS BOUND fired (Timeout → .Catch → EnterInitializationFailedState),
        // not merely the startup-timer's backlog drain (which never sets this).
        ((MessageHub)host).InitializationError.Should().NotBeNull("the hung hub records the FAILED status");
    }
}
