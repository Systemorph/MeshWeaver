using System;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Deterministic repro of the TRUE root behind the <c>SidePanelChatTenMessagesTest</c> round-3
/// composer-vanish: <see cref="DataExtensions"/>.<c>RouteStreamMessage</c> walks the hub's
/// parent chain (<c>current = current.Configuration.ParentHub</c>) looking for the sync sub-hub
/// for a <see cref="StreamMessage"/>'s StreamId. A root/mesh hub is a SELF-PARENT — its
/// <c>ParentServiceProvider</c> resolves <c>IMessageHub</c> to itself — so when the sync sub-hub
/// is ABSENT (a disconnected circuit's stream, a reaped/never-created sync hub), the walk NEVER
/// advances: <c>current</c> stays the same hub forever. A SINGLE <c>DataChangedEvent</c> then
/// spins that hub's <c>DrainOne</c> thread at 100% CPU inside <c>GetHostedHub</c> → the hub can
/// process nothing else → its SignalR keepalive is starved (chat vanishes) and it becomes an
/// undisposable, silently-pegged zombie (the 8s disposal-deadlock watchdog cannot interrupt a
/// running synchronous loop). A live dotnet-stack of the wedged e2e pod showed exactly this
/// single frame (<c>DrainOne → NotifyAsync → RouteMessageAsync → RouteStreamMessage →
/// GetHostedHub → AddressComparer.GetHashCode</c>), persisting even when idle.
///
/// <para>The fix adds the self-parent termination guard the sibling walk
/// (<c>MessageHubExtensions.BeginAsyncOperation</c>) already has. With it, the not-found walk
/// terminates and the message is dropped (Ignored) — the drain stays responsive. This test posts
/// the poison StreamMessage to a self-parent, AddData-wired mesh hub and asserts the drain still
/// answers a follow-up request; without the guard the follow-up never returns (the drain wedges).</para>
/// </summary>
public class StreamRouteSelfParentTerminationTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record Ping : IRequest<Pong>;
    public record Pong;

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf)
            // AddData wires RouteStreamMessage onto the mesh hub (the self-parent root).
            .AddData()
            .WithTypes(typeof(Ping), typeof(Pong))
            .WithHandler<Ping>((hub, request) =>
            {
                hub.Post(new Pong(), o => o.ResponseFor(request));
                return request.Processed();
            });

    [Fact]
    public async Task StreamMessageToSelfParentHub_WithAbsentSyncHub_DoesNotWedgeTheDrain()
    {
        // The repro requires the production self-parent shape: the mesh hub's ParentHub is itself.
        Mesh.Configuration.ParentHub.Should().BeSameAs(Mesh,
            "a root/mesh hub resolves its own IMessageHub as ParentHub — the self-parent chain that "
            + "makes RouteStreamMessage's walk non-terminating");

        // Warm up: prove the drain is live (and initialization has completed) before the poison.
        (await Mesh.Observe(new Ping(), o => o.WithTarget(Mesh.Address))
            .Should().Within(15.Seconds()).Emit())
            .Message.Should().BeOfType<Pong>();

        // POISON: a StreamMessage (DataChangedEvent) targeted at the mesh, for a StreamId that has
        // NO sync sub-hub. RouteStreamMessage walks the parent chain; on the self-parent mesh hub
        // the walk must TERMINATE (drop the message) instead of spinning DrainOne forever.
        Mesh.Post(
            new DataChangedEvent(Guid.NewGuid().AsString(), 1, new RawJson("{}"), ChangeType.Full, null),
            o => o.WithTarget(Mesh.Address));

        // The drain must still answer — a wedged (infinitely-walking) DrainOne would never reach
        // this Ping, and the Emit() would time out. This is the assertion that fails without the guard.
        (await Mesh.Observe(new Ping(), o => o.WithTarget(Mesh.Address))
            .Should().Within(10.Seconds()).Emit())
            .Message.Should().BeOfType<Pong>(
                "the mesh drain must stay responsive after routing a StreamMessage whose sync sub-hub is absent");
    }
}
