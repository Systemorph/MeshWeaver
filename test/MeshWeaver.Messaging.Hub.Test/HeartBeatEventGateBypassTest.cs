using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the framework-level guarantee that <see cref="HeartBeatEvent"/>
/// (and the other system messages — see <c>MessageService.cs</c>) always pass
/// every <c>WithInitializationGate(...)</c> regardless of the user's predicate.
///
/// <para><b>Why this exists.</b> Heartbeats are the liveness signal between
/// hubs. If a per-gate predicate could queue them behind a closed gate, a hub
/// stuck in initialization would appear dead to its peers and routing would
/// silently fail. The fix moved <c>HeartBeatEvent</c> into the framework's
/// hardcoded bypass list in <c>MessageService.cs</c> alongside
/// <c>InitializeHubRequest</c> / <c>ShutdownRequest</c> / <c>DisposeRequest</c> /
/// <c>DeliveryFailure</c> so no per-gate predicate can re-introduce the
/// regression.</para>
///
/// <para><b>The test.</b> Configure a host with a gate whose predicate is
/// <c>_ =&gt; false</c> (never lets anything through, never opens) and register
/// a <see cref="HeartBeatEvent"/> handler that flips a TCS. Post a heartbeat
/// from the test. If the bypass holds, the handler runs and the TCS completes;
/// if not, the message is queued behind the closed gate forever.</para>
/// </summary>
public class HeartBeatEventGateBypassTest(ITestOutputHelper output) : HubTestBase(output)
{
    // ReplaySubject(1): the host's HeartBeatEvent handler may fire OnNext before
    // the test's blocking .Should() subscribes, so a hot Subject would drop the
    // emission. Replay guarantees the signal is observed regardless of ordering.
    private readonly ReplaySubject<HeartBeatEvent> _heartBeatHandled = new(1);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            // Override the framework's permissive predicate for the InitializeGateName
            // so the per-gate path can NO LONGER rescue heartbeats, and add a second
            // user-defined gate that also never opens. With both gates rejecting, the
            // only thing that can let HeartBeatEvent pass is the hardcoded
            // `delivery.Message is … or HeartBeatEvent …` short-circuit in
            // MessageService.cs. If that short-circuit is removed/regressed, the
            // posted heartbeat is queued forever and this test hangs.
            .WithInitializationGate(MessageHubConfiguration.InitializeGateName, _ => false)
            .WithInitializationGate("test-never-opens", _ => false)
            // BuildupActions run on InitializeHubRequest, not heartbeats — so we use a
            // handler as the signal that HeartBeatEvent reached the host's processing
            // pipeline. If HeartBeatEvent were queued behind the gates, this never fires.
            .WithHandler<HeartBeatEvent>((hub, request) =>
            {
                _heartBeatHandled.OnNext(request.Message);
                return request.Processed();
            });

    [Fact]
    public void HeartBeatEvent_BypassesNeverOpeningGate()
    {
        var host = GetHost();
        host.Should().NotBeNull();

        // Post a heartbeat to the host. With the bypass, the host's HeartBeatEvent
        // handler runs and pushes the signal. Without the bypass, the message is
        // queued behind the never-opening gates and the wait times out.
        host.Post(new HeartBeatEvent(), o => o.WithTarget(host.Address));

        // 5s is generous; with the bypass this completes promptly after delivery.
        // Without the bypass the gate stays closed forever and this fails on timeout.
        // The blocking .Should().Emit() asserts the handler observed the heartbeat:
        // HeartBeatEvent must bypass every WithInitializationGate predicate so
        // liveness signals reach hubs even while initialization gates are closed.
        // If this fails, the framework-level bypass list in MessageService.cs has
        // regressed — see Doc/Architecture/InitializationGates.md →
        // 'Framework-bypassed messages'.
        _heartBeatHandled.Should().Within(5.Seconds()).Emit();
    }
}
