namespace MeshWeaver.Graph.Security;

/// <summary>
/// 🛡️ When a node's <c>MainNode</c> may be trusted for an access decision (review of #7601). The main
/// node is a field the writer supplies, and two seams delegate on it. The own-main-node shortcut
/// (<see cref="RlsNodeValidator"/>) trusts it only when it IS the node or one of its ANCESTORS. Satellite
/// delegation (<see cref="SatelliteAccessRule"/>) also requires the path check where a node lands when
/// a WRITER chooses a pointer outside its ancestry; a stored off-partition pointer still governs reads.
/// </summary>
public static class MainNodeTrust
{
    /// <summary>Whether <paramref name="mainNode"/> is <paramref name="path"/> itself or one of its
    /// ancestors. Case-insensitive on segments, as paths resolve.</summary>
    /// <param name="mainNode">The node's main node.</param>
    /// <param name="path">The node's path.</param>
    public static bool IsAncestorOrSelf(string? mainNode, string? path)
    {
        if (string.IsNullOrEmpty(mainNode) || string.IsNullOrEmpty(path))
            return false;
        var main = mainNode.Trim('/');
        var node = path.Trim('/');
        return string.Equals(main, node, StringComparison.OrdinalIgnoreCase)
               || node.StartsWith(main + "/", StringComparison.OrdinalIgnoreCase);
    }
}
