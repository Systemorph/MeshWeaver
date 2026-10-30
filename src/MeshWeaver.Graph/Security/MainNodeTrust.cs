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
    /// The satellite's OWNER: the path before its own satellite segment — the LAST segment above the
    /// leaf that starts with <c>_</c> — e.g. <c>Space/Doc</c> for <c>Space/Doc/_Comment/c1</c>; the
    /// parent when the path carries no such segment. Null for a single-segment path.
    /// <para>The LAST, never the first (review of #7601, round 3): an unmapped <c>_</c> segment higher
    /// up is ordinary content, and a scope below it can break the inherited grant. For
    /// <c>Space/_ledger/Private/_Comment/c1</c> the owner is <c>Space/_ledger/Private</c>, so a pointer
    /// to <c>Space</c> must also pass the landing check. Erring deeper only ever adds that check.</para>
    /// </summary>
    /// <param name="path">The satellite's path.</param>
    public static string? OwnerOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var segments = path.Trim('/').Split('/');
        var own = segments.Length < 2 ? -1 : Array.FindLastIndex(segments, segments.Length - 2, s => s.StartsWith('_'));
        var take = own > 0 ? own : own == 0 ? 0 : segments.Length - 1;
        return take <= 0 ? null : string.Join('/', segments, 0, take);
    }

    /// <summary>
    /// Whether <paramref name="mainNode"/> is exactly the satellite's owner (<see cref="OwnerOf"/>),
    /// compared ordinally. Only that pointer is the one the satellite's location already implies; an
    /// ANCESTOR further up is not, because a scope in between can break the inherited grant
    /// (<c>_Policy</c> with <c>BreaksInheritance</c>), and a case-variant path is another node.
    /// </summary>
    /// <param name="mainNode">The main node the writer chose.</param>
    /// <param name="path">The satellite's path.</param>
    public static bool IsOwnerOf(string? mainNode, string? path) =>
        !string.IsNullOrEmpty(mainNode)
        && OwnerOf(path) is { } owner
        && string.Equals(mainNode.Trim('/'), owner, StringComparison.Ordinal);
}
