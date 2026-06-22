using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Connection.SignalR.Test;

/// <summary>
/// The keystone of multi-remote SignalR: given N connected remote meshes (each keyed by its portal
/// address), the route picks the connection whose remote owns the target — matched by the target's
/// host chain. This pins that pure selection logic without needing a live socket.
/// </summary>
public class SignalRMultiRemoteSelectionTest
{
    private static Address Portal(string id) => AddressExtensions.CreatePortalAddress(id);

    // A node that lives on a remote portal carries that portal in its host chain.
    private static Address NodeOn(Address remote) => new Address("Markdown", "Welcome").WithHost(remote);

    [Fact]
    public void Each_target_routes_to_its_owning_remote()
    {
        var a = Portal("memex");
        var b = Portal("atioz");
        var remotes = new Address?[] { a, b };

        SignalRRemoteConnections.SelectIndex(remotes, NodeOn(a)).Should().Be(0);
        SignalRRemoteConnections.SelectIndex(remotes, NodeOn(b)).Should().Be(1);
    }

    [Fact]
    public void A_target_that_is_a_remote_itself_selects_it()
    {
        var a = Portal("memex");
        SignalRRemoteConnections.SelectIndex(new Address?[] { Portal("atioz"), a }, a).Should().Be(1);
    }

    [Fact]
    public void A_target_owned_by_no_connected_remote_selects_none()
    {
        var remotes = new Address?[] { Portal("memex"), Portal("atioz") };
        SignalRRemoteConnections.SelectIndex(remotes, NodeOn(Portal("other"))).Should().BeNull();
    }

    [Fact]
    public void A_single_unkeyed_remote_serves_every_target()
    {
        // Backward compatibility: one connection registered with no remote address routes everything.
        var remotes = new Address?[] { null };
        SignalRRemoteConnections.SelectIndex(remotes, NodeOn(Portal("anything"))).Should().Be(0);
        SignalRRemoteConnections.SelectIndex(remotes, Portal("anything")).Should().Be(0);
    }

    [Fact]
    public void No_remotes_or_null_target_selects_none()
    {
        SignalRRemoteConnections.SelectIndex(Array.Empty<Address?>(), Portal("x")).Should().BeNull();
        SignalRRemoteConnections.SelectIndex(new Address?[] { Portal("x") }, null).Should().BeNull();
    }
}
