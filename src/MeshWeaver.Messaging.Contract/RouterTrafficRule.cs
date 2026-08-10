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
    /// <param name="isResponse">Whether the delivery ANSWERS a request (it carries a request-id
    /// correlation). A response the router posts is routing's own duty — the undeliverable-mail
    /// NACK (<c>RoutingServiceBase.PostNotFound</c> / <c>NackRouteFailure</c> post their
    /// <c>DeliveryFailure</c> from the mesh hub via <c>ResponseFor</c>, so its sender is honestly
    /// <c>mesh/{id}</c>) — not work, and reporting it trains people to mute the channel. Coverage
    /// is not lost: real work SENT TO the router is still reported at request time via the
    /// <c>"target"</c> role, and the payload type is opaque at the detector anyway (a routed NACK
    /// arrives packed as <c>RawJson</c>), so the correlation marker is the structural signal.</param>
    /// <returns><c>"target"</c>, <c>"sender"</c>, <c>"sender AND target"</c>, or <c>null</c>.</returns>
    public static string? RoleOf(string? targetAddressType, string? senderAddressType, object? message,
        bool isResponse)
    {
        // Heartbeats ARE the router's own job — routing liveness, not work. Type check, not a name
        // match: a rename must not silently turn this exclusion off.
        if (message is HeartBeatEvent)
            return null;

        var targetIsRouter = string.Equals(targetAddressType, AddressExtensions.MeshType, StringComparison.Ordinal);
        var senderIsRouter = string.Equals(senderAddressType, AddressExtensions.MeshType, StringComparison.Ordinal);

        // The router ANSWERING (and not also being the target) is the routing NACK — its own job.
        // A response ADDRESSED AT the router still reports: it proves the router issued a request,
        // which is the violation the issuing seam (NodeOperationIssuingHub) exists to remove.
        if (senderIsRouter && !targetIsRouter && isResponse)
            return null;

        return (targetIsRouter, senderIsRouter) switch
        {
            (true, true) => "sender AND target",
            (true, false) => "target",
            (false, true) => "sender",
            _ => null,
        };
    }

    /// <summary>
    /// Binary-compatible 3-argument form — the pre-<c>isResponse</c> public signature, kept so
    /// assemblies compiled against it keep resolving (this is a shipped Contract package).
    /// Treats the delivery as a non-response, i.e. applies no NACK exclusion.
    /// </summary>
    /// <param name="targetAddressType">Address type of the delivery's TARGET.</param>
    /// <param name="senderAddressType">Address type of the sender, if any.</param>
    /// <param name="message">The message being delivered.</param>
    /// <returns><c>"target"</c>, <c>"sender"</c>, <c>"sender AND target"</c>, or <c>null</c>.</returns>
    public static string? RoleOf(string? targetAddressType, string? senderAddressType, object? message)
        => RoleOf(targetAddressType, senderAddressType, message, isResponse: false);
}
