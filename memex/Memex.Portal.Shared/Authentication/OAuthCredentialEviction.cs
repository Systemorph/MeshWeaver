namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The eviction rule of the OAuth token exchange, kept pure so it can be pinned without a mesh:
/// which of a client's credentials a fresh authorization removes.
/// </summary>
internal static class OAuthCredentialEviction
{
    /// <summary>
    /// The paths a fresh authorization evicts, oldest first. Candidates are the tokens carrying
    /// <paramref name="label"/> that are strictly OLDER than the one just minted, in a total order over
    /// <c>(CreatedAt, path)</c>. Of those, the newest <c><paramref name="maxLive"/> - 1</c> LIVE ones
    /// are kept — so, with the token just minted, at most <paramref name="maxLive"/> stay live — and
    /// every other older candidate is evicted. A revoked or expired row is not live: it never takes one
    /// of the kept slots and is always removed, even when it is NEWER than the token just minted.
    ///
    /// <para>Only strictly-older LIVE rows are ever evicted, never "everything that is not mine": two
    /// concurrent exchanges each see the other's fresh token, and a not-mine rule would have them delete
    /// each other's. Ranking relative to the evaluating exchange keeps the outcome convergent: a token
    /// among the <paramref name="maxLive"/> newest has at most <c>maxLive - 2</c> tokens between it and
    /// any newer exchange, so no exchange evicts it, and a listing that lags (misses a row) only ranks
    /// older rows higher — it evicts less, never a newer token.</para>
    /// </summary>
    internal static IReadOnlyList<string> Evict(
        IEnumerable<ApiTokenInfo> tokens, string label, string keepPath, DateTimeOffset mintedAt, int maxLive)
    {
        var keepOlder = Math.Max(1, maxLive) - 1;
        var candidates = tokens
            .Where(t => t.Label == label && t.NodePath != keepPath)
            .ToArray();

        // The kept slots go ONLY to live rows strictly older than the token just minted — a newer
        // live row belongs to a concurrent exchange and is never this exchange's to judge.
        var kept = candidates
            .Where(t => IsLive(t, mintedAt) && IsOlder(t, keepPath, mintedAt))
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.NodePath, StringComparer.Ordinal)
            .Take(keepOlder)
            .Select(t => t.NodePath)
            .ToHashSet(StringComparer.Ordinal);

        // Evicted: every older row outside the kept slots, and every DEAD row whatever its age — a
        // revoked or expired credential opens nothing, so removing one is never a race with its
        // holder, even when a concurrent exchange minted it after this one.
        return candidates
            .Where(t => !kept.Contains(t.NodePath)
                        && (IsOlder(t, keepPath, mintedAt) || !IsLive(t, mintedAt)))
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.NodePath, StringComparer.Ordinal)
            .Select(t => t.NodePath)
            .ToArray();
    }

    private static bool IsOlder(ApiTokenInfo t, string keepPath, DateTimeOffset mintedAt) =>
        t.CreatedAt < mintedAt
        || (t.CreatedAt == mintedAt && string.CompareOrdinal(t.NodePath, keepPath) < 0);

    private static bool IsLive(ApiTokenInfo t, DateTimeOffset mintedAt) =>
        !t.IsRevoked && !(t.ExpiresAt <= mintedAt);
}
