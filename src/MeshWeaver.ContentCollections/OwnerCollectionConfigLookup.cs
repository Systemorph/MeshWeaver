using System.Reactive.Linq;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Asks the hub that OWNS a collection for its configuration. A collection is declared on a node's
/// own hub (<c>MapContentCollection</c> / <c>AddContentCollection</c>), so a client — the portal's
/// file browser above all — that holds only a collection NAME cannot open it from its own content
/// service: it must first fetch the owner's configuration and register it (triage fb-6e9625ee: a
/// <c>FileBrowserControl("attachments")</c> carrying no configuration read "could not be opened on
/// this portal" on every post and event page).
/// </summary>
public static class OwnerCollectionConfigLookup
{
    /// <summary>
    /// The configuration of <paramref name="collection"/> as the hub at <paramref name="owner"/> declares
    /// it, or <c>null</c> when that hub declares no such collection (or has no content handler, which
    /// answers nothing and surfaces as the timeout). Cold: the request is posted on Subscribe, under the
    /// identity ambient then. A timeout or a delivery failure FAULTS — it is not "the node declares none",
    /// and the caller says which.
    /// </summary>
    /// <param name="issuer">The hub that posts the request (issued off the router).</param>
    /// <param name="owner">The address of the hub that owns the collection.</param>
    /// <param name="collection">The collection name.</param>
    /// <param name="timeout">How long the owner gets to answer.</param>
    /// <param name="caller">
    /// An identity to stamp on the read, or null for the ambient one. The read is gated by the owning
    /// hub's <c>[RequiresPermission(Read)]</c> either way, and a read with NO identity at all is refused
    /// (no silent anonymous read) — the portal's file browser subscribes under the circuit viewer.
    /// </param>
    public static IObservable<ContentCollectionConfig?> GetOwnerCollectionConfig(
        this IMessageHub issuer, Address owner, string collection, TimeSpan timeout, AccessContext? caller = null)
        => ContentFileResolver.ObserveCollectionConfigs(issuer, owner, [collection], caller)
            .Timeout(timeout)
            .Select(delivery => ContentFileResolver.ReadCollectionConfigs(delivery)?
                .FirstOrDefault(c => string.Equals(c.Name, collection, StringComparison.Ordinal)));
}
