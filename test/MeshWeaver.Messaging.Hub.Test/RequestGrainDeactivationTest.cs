using System.Threading;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the #147 out-of-band grain deactivation escape hatch
/// (<see cref="MessageHubExtensions.RequestGrainDeactivation"/>). The Orleans grain
/// registers a <see cref="GrainDeactivateCallback"/> on its top-level hub's
/// configuration during activation; the stuck-round watchdog invokes it when its
/// force-Idle rescue write cannot land (the hub's action block — running on the
/// grain's ActivationTaskScheduler — is wedged, so message-shaped rescues join the
/// blocked backlog forever). Three contracts:
/// <list type="bullet">
/// <item>monolith hosting (no callback anywhere) → <c>false</c>, a safe no-op;</item>
/// <item>callback on the hub itself (the grain topology) → invoked, <c>true</c>;</item>
/// <item>callback on an ancestor, invoked from a child hub (threads' <c>_Exec</c>,
/// message cells) → found via the parent-chain walk, same as
/// <c>BeginAsyncOperation</c> / the HeartBeatEvent handler.</item>
/// </list>
/// </summary>
public class RequestGrainDeactivationTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Fact]
    public void NoCallbackRegistered_MonolithHosting_ReturnsFalse()
    {
        // No grain hosting → no GrainDeactivateCallback anywhere in the chain. The
        // watchdog treats false as "no escape hatch available" and relies on the
        // rescue write alone — there is no grain scheduler to wedge in monolith.
        var hub = Mesh.GetHostedHub(new Address("plainhub", "1"), x => x, HostedHubCreation.Always)!;
        hub.RequestGrainDeactivation().Should().BeFalse();
    }

    [Fact]
    public void CallbackOnOwnConfiguration_IsInvokedOnce()
    {
        // Mirrors MessageHubGrain.CompleteActivation: the callback is Set on the
        // grain's top-level hub configuration.
        var invoked = 0;
        var hub = Mesh.GetHostedHub(new Address("grainhub", "1"),
            c => c.Set(new GrainDeactivateCallback(() => Interlocked.Increment(ref invoked))),
            HostedHubCreation.Always)!;

        hub.RequestGrainDeactivation().Should().BeTrue();
        invoked.Should().Be(1);
    }

    [Fact]
    public void CallbackOnAncestor_FoundFromChildHub()
    {
        // The watchdog may run on a hub BELOW the grain's top-level hub (e.g. a
        // thread's _Exec hosted hub). The extension must walk the parent chain,
        // exactly like BeginAsyncOperation / HandleHeartBeat.
        var invoked = 0;
        var parent = Mesh.GetHostedHub(new Address("grainhub", "parent"),
            c => c.Set(new GrainDeactivateCallback(() => Interlocked.Increment(ref invoked))),
            HostedHubCreation.Always)!;
        var child = parent.GetHostedHub(new Address("execchild", "1"), x => x, HostedHubCreation.Always)!;

        child.RequestGrainDeactivation().Should().BeTrue();
        invoked.Should().Be(1);
    }
}
