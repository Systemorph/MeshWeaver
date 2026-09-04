namespace MeshWeaver.Messaging;

/// <summary>
/// A last-resort taker for a CORRELATED REPLY that routing cannot deliver — the answer to a request
/// somebody in THIS process is still waiting for, which the hierarchy is about to drop.
///
/// <para>🚨 <b>Why this exists.</b> During a whole-mesh teardown the owner answers its request
/// normally: it posts the reply, its own <c>Post</c> accepts it (its run level is still open), and
/// one turn later <see cref="HierarchicalRouting"/> finds the parent past
/// <c>DisposeHostedHubs</c> and drops it — classified, logged, and told to nobody:</para>
/// <code>
/// Message delivery failed for PatchDataResponse (ID: l9ret6X…) in TestData/teardown-nack-node:
///   Hub TestData/teardown-nack-node cannot route PatchDataResponse to cache/VLe3ZpQ…
/// </code>
/// <para>The waiter is in the SAME process — its registry entry is armed and could take the reply
/// directly — but the routing layer has no way to reach it, so the caller burns its whole budget in
/// silence. Measured repeatedly on <c>NackReachesTheWaiterDuringTeardownTest</c> (core CI runs
/// 33861078925 and 33865385033, and locally under load), which is precisely this shape.</para>
///
/// <para>🚨 <b>This is NOT a second transport for live traffic.</b> It is offered ONLY where the
/// delivery is being dropped — where a post can no longer reach the waiter by any route. Serving it
/// alongside a healthy post reorders the answer ahead of the state it acknowledges: tried twice on
/// the patch-verdict seam and reverted both times (<c>ComboGateRollTest</c>,
/// <c>ImportTypeBeforeInstanceTest</c>). Here there is no post left to race.</para>
///
/// <para>Implemented by the mesh's late-verdict registry and resolved from the hub's service
/// provider; a mesh without one simply drops as before.</para>
/// </summary>
public interface IUndeliverableReplySink
{
    /// <summary>
    /// Offer <paramref name="delivery"/> — a reply carrying <c>PostOptions.RequestId</c> — to a
    /// local waiter armed for that correlation.
    /// </summary>
    /// <param name="delivery">The reply routing is about to drop.</param>
    /// <returns><c>true</c> when a local waiter took it, so the caller HAS been answered;
    /// <c>false</c> when nobody here was waiting, and the drop stands.</returns>
    bool TryDeliver(IMessageDelivery delivery);
}
