using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// 🛡️ When a node's <c>MainNode</c> may be trusted for an access decision (review of #7601). The main
/// node is a field the writer supplies, and two seams delegate on it. The own-main-node shortcut
/// (<see cref="RlsNodeValidator"/>) trusts it only when it IS the node or one of its ANCESTORS. Satellite
/// delegation (<see cref="SatelliteAccessRule"/>) also requires the path check where a node lands when
/// a WRITER chooses a pointer other than the satellite's OWNER (<see cref="IsOwnerOf"/>); a stored
/// pointer still governs reads.
/// </summary>
public static class MainNodeTrust
{
    /// <summary>Whether <paramref name="mainNode"/> is <paramref name="path"/> itself or one of its
    /// ancestors. Ordinal on segments: paths and permission scopes are keyed ordinally, so a
    /// case-variant path is a DIFFERENT node, never an ancestor.</summary>
    /// <param name="mainNode">The node's main node.</param>
    /// <param name="path">The node's path.</param>
    public static bool IsAncestorOrSelf(string? mainNode, string? path)
    {
        if (string.IsNullOrEmpty(mainNode) || string.IsNullOrEmpty(path))
            return false;
        var main = mainNode.Trim('/');
        var node = path.Trim('/');
        return string.Equals(main, node, StringComparison.Ordinal)
               || node.StartsWith(main + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// The satellite's OWNER: the path before its own satellite segment when that segment holds the
    /// node DIRECTLY — the segment right above the leaf starts with <c>_</c> — e.g. <c>Space/Doc</c>
    /// for <c>Space/Doc/_Comment/c1</c>; otherwise the PARENT. Null for a single-segment path, or one
    /// whose satellite segment is the first.
    /// <para>Only the segment directly above the leaf (review of #7601, rounds 3 and 4). An unmapped
    /// <c>_</c> segment higher up is ordinary content (<c>Space/_ledger/Private/c1</c> is owned by
    /// <c>Space/_ledger/Private</c>), and a node nested inside a satellite
    /// (<c>Space/Doc/_Comment/c1/reply</c>) is owned by that satellite, since a scope in between can
    /// break the inherited grant. A pointer further up must then also pass the landing check. Erring
    /// deeper only ever adds that check, which a legitimate writer passes through ordinary
    /// inheritance, so it cannot widen anything.</para>
    /// </summary>
    /// <param name="path">The satellite's path.</param>
    public static string? OwnerOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var segments = path.Trim('/').Split('/');
        if (segments.Length < 2)
            return null;
        var take = segments[^2].StartsWith('_') ? segments.Length - 2 : segments.Length - 1;
        return take <= 0 ? null : string.Join('/', segments, 0, take);
    }

    /// <summary>
    /// Whether <paramref name="mainNode"/> is EXACTLY the satellite's owner (<see cref="OwnerOf"/>):
    /// the raw value, compared ordinally, with no trimming and no case folding. Only that pointer is the
    /// one the satellite's location implies. An ANCESTOR further up is not, because a scope in between
    /// can break the inherited grant (<c>_Policy</c> with <c>BreaksInheritance</c>). A case variant, a
    /// trailing slash or a <c>..</c> segment names another scope to the permission fold, so none of them
    /// is the owner (review of #7655).
    /// </summary>
    /// <param name="mainNode">The main node the writer chose.</param>
    /// <param name="path">The satellite's path.</param>
    public static bool IsOwnerOf(string? mainNode, string? path) =>
        !string.IsNullOrEmpty(mainNode)
        && OwnerOf(path) is { } owner
        && string.Equals(mainNode, owner, StringComparison.Ordinal);

    /// <summary>
    /// 🛡️ Whether writing <paramref name="after"/> over the stored <paramref name="before"/> REPOINTS
    /// the node's main node. THE INVARIANT (review of #7655): a non-System write commits a satellite's
    /// <c>MainNode</c> either UNCHANGED (the raw stored value, byte for byte) or, when the stored pointer
    /// was never set (empty, or the node itself), as EXACTLY its path-derived security owner
    /// (<see cref="IsOwnerOf"/>). Nothing else.
    ///
    /// <para>There is deliberately no other "normalisation" exception. The storage owner of the first
    /// satellite segment (<c>SatelliteTableMapping.OwnerOfSatellitePath</c>, what the platform's create
    /// and upsert normalisation writes) sits ABOVE a nested satellite's security owner
    /// (<c>Doc</c> vs <c>Doc/_Thread/t1</c> for <c>Doc/_Thread/t1/_Comment/c1</c>), so accepting it let
    /// every route that can produce an unset pointer (a create that kept <c>""</c>, a retype, a self
    /// pointer) move delegation above a policy on the thread. Values are RAW: <c>""</c> and <c>"/"</c>
    /// differ (the rule falls back to the node's own path for the first and delegates the second to the
    /// root scope), and so do case variants and trailing slashes.</para>
    /// </summary>
    /// <param name="before">The stored node.</param>
    /// <param name="after">The node as it is about to be written.</param>
    /// <returns>True when the write commits any other main node.</returns>
    public static bool Repoints(MeshNode before, MeshNode after)
    {
        var previous = before.MainNode ?? "";
        var next = after.MainNode ?? "";
        if (string.Equals(previous, next, StringComparison.Ordinal))
            return false;
        var previousWasUnset = previous.Length == 0
                               || string.Equals(previous, before.Path, StringComparison.Ordinal);
        return !(previousWasUnset && IsOwnerOf(next, after.Path));
    }
}
