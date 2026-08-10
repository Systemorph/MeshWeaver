namespace MeshWeaver.Messaging;

/// <summary>
/// Whether a delivery involves the ROOT MESH HUB — the router — as an end, and in which role.
///
/// <para>A pure predicate, decidable from three values with no hub, no logger and no delivery
/// pipeline. The detector's whole value is that it fires on exactly the right traffic: a false
/// ERROR trains people to mute the channel, and a missed one is how a router-starvation wedge stays
/// invisible. That is worth pinning directly rather than through a running hub.</para>
///
/// <para>🚨 "An END" means an end of the DELIVERY — the address it is addressed TO and the address
/// it came FROM. It is NOT the hub currently handling it: every hosted hub's non-local delivery is
/// routed UP through <c>parentHub.DeliverMessage(delivery)</c>, and that parent is almost always the
/// root mesh hub, so feeding this the RECEIVING hub's address type reports every routing HOP — the
/// router's actual job — as a violation.</para>
/// </summary>
public static class RouterTrafficRule
{
    /// <summary>
    /// The role the mesh hub plays in this delivery, or <c>null</c> when it is not an end of it (or
    /// when the message is routing liveness rather than work).
    /// </summary>
    /// <param name="targetAddressType">
    /// Address type of the delivery's TARGET — the address it is addressed to. Never the address
    /// type of the hub that happens to be handling the delivery: a hub the delivery is merely
    /// routed THROUGH is not an end of it.
    /// </param>
    /// <param name="senderAddressType">Address type of the sender, if any.</param>
    /// <param name="message">The message being delivered.</param>
    /// <returns><c>"target"</c>, <c>"sender"</c>, <c>"sender AND target"</c>, or <c>null</c>.</returns>
    public static string? RoleOf(string? targetAddressType, string? senderAddressType, object? message)
    {
        // Heartbeats ARE the router's own job — routing liveness, not work. Type check, not a name
        // match: a rename must not silently turn this exclusion off.
        if (message is HeartBeatEvent)
            return null;

        var targetIsRouter = string.Equals(targetAddressType, AddressExtensions.MeshType, StringComparison.Ordinal);
        var senderIsRouter = string.Equals(senderAddressType, AddressExtensions.MeshType, StringComparison.Ordinal);

        return (targetIsRouter, senderIsRouter) switch
        {
            (true, true) => "sender AND target",
            (true, false) => "target",
            (false, true) => "sender",
            _ => null,
        };
    }
}
