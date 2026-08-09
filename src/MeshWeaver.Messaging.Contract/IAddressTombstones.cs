namespace MeshWeaver.Messaging;

/// <summary>
/// Tells the message pipeline whether an address is gone <b>for good</b> — the one fact a
/// disposing hub cannot otherwise know about itself.
///
/// <para>🚨 Why the pipeline needs it. When a hub is past <c>DisposeHostedHubs</c> it drops every non-shutdown delivery and
/// NACKs the sender as the TRANSIENT <see cref="ErrorType.ShuttingDown"/> — deliberately, because
/// a hub going down for a recycle/restart WILL reactivate and consumers with their own recovery
/// machinery (<c>JsonSynchronizationStream</c>'s change-feed resubscribe latch) must ride that out
/// rather than tear down. But the very same drop fires when the hub is going down because its NODE
/// WAS DELETED, and then "transient, retry" is a lie no caller can act on: the address will never
/// reactivate, no change-feed announce will ever arrive, and a consumer that rides it out is left
/// with no verdict at all — the read burns its whole budget and reports "unavailable" for a node
/// that is provably gone (issue #1029), while its keep-alive goes on heartbeating a nonexistent
/// owner forever.</para>
///
/// <para>Implemented over the mesh's delete tombstone (<c>RecentlyDeletedRegistry</c>), which the
/// delete handler populates SYNCHRONOUSLY — every planned path is marked before the delete's
/// response returns — so a lookup here is authoritative at exactly the moment a racing delivery
/// arrives at the dying hub. A legitimate re-create clears the tombstone, so a recycled or
/// re-created address is never reported as deleted.</para>
///
/// <para>Optional: meshes without a delete tombstone (bare messaging fixtures) register no
/// implementation and the pipeline keeps its historical <see cref="ErrorType.ShuttingDown"/>
/// classification.</para>
/// </summary>
public interface IAddressTombstones
{
    /// <summary>
    /// True when the node at <paramref name="path"/> was deleted and has not been re-created
    /// since — i.e. the address is gone for good, not merely restarting.
    /// </summary>
    /// <param name="path">The node path to test (a hub address's <c>Path</c>).</param>
    /// <returns><c>true</c> when a live delete tombstone covers the path.</returns>
    bool IsDeleted(string? path);
}
