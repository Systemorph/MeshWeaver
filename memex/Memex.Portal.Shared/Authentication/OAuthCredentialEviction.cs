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
    /// every other candidate is evicted. A revoked or expired row is not live: it never takes one of the
    /// kept slots and is always removed.
    ///
    /// <para>Only strictly-older rows are ever candidates, never "everything that is not mine": two
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
        var older = tokens
            .Where(t => t.Label == label && t.NodePath != keepPath)
            .Where(t => t.CreatedAt < mintedAt
                        || (t.CreatedAt == mintedAt && string.CompareOrdinal(t.NodePath, keepPath) < 0))
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.NodePath, StringComparer.Ordinal)
            .ToArray();

        var kept = older
            .Where(t => !t.IsRevoked && !(t.ExpiresAt <= mintedAt))
            .Take(keepOlder)
            .Select(t => t.NodePath)
            .ToHashSet(StringComparer.Ordinal);

        return older
            .Where(t => !kept.Contains(t.NodePath))
            .Reverse()
            .Select(t => t.NodePath)
            .ToArray();
    }
}
