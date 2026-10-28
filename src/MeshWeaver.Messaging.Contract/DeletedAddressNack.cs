namespace MeshWeaver.Messaging;

/// <summary>
/// The one authoritative "this address is gone for good" NACK sentence — the
/// <see cref="ErrorType.NotFound"/> answer a hub going down because its node was DELETED gives a
/// delivery it can no longer act on (see <see cref="IAddressTombstones"/>).
///
/// <para>🚨 THE WORDING IS CONTRACT, not prose, and it lives in ONE place for that reason: every
/// site that answers a teardown-time delivery for a tombstoned address (the message service's
/// abandoned-delivery NACK and its accepted-then-faulted catch, the access gate's release of a
/// verdict the teardown cut off) composes it here. It MUST contain <c>"No node found"</c> — matched
/// by <c>MeshNodeStreamCache.IsMissingNodeFailure</c>, which turns it into a definitive absence —
/// and MUST NOT contain any transient marker (<c>"is shutting down"</c>, <c>"Rejecting now"</c>,
/// …), or every reader goes back to re-probing an address that will never answer (#1029). A drift
/// is silent: pinned by <c>DeletedAddressNackClassificationTest</c>.</para>
/// </summary>
public static class DeletedAddressNack
{
    /// <summary>The NACK message for a delivery to <paramref name="path"/>, whose node was deleted.</summary>
    /// <param name="path">The deleted address's path.</param>
    /// <returns>The authoritative, non-transient "No node found" sentence.</returns>
    public static string Message(string? path) =>
        $"No node found at '{path}' — the node was deleted, so this address "
        + "will not reactivate.";
}
