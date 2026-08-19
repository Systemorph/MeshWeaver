using System;
using System.Collections.Generic;
using System.Linq;

namespace MeshWeaver.Social;

/// <summary>
/// Deciding WHICH live network post a mesh post is — the pure half of the history sync, split out
/// so the matching rules are testable without an API round trip.
///
/// <para>🚨 <b>Match-and-update only.</b> Nothing here creates a post. A network account holds years
/// of history the mesh never authored, and importing it would bury a working space under hundreds of
/// nodes nobody asked for. The job's whole purpose is to fill in what the mesh already believes:
/// which live post a node became, when it went out, and how it did.</para>
///
/// <para>🚨 <b>A wrong match is worse than no match.</b> Attaching the wrong URN to a node makes
/// every later stat lookup read someone else's numbers, and the mistake is invisible — the counts
/// look plausible. So the rule is deliberately conservative: an EXACT normalised-text match, and
/// only when exactly ONE candidate has it. Two candidates that both match, or none, both mean "leave
/// it alone".</para>
/// </summary>
public static class SocialHistoryMatch
{
    /// <summary>
    /// How much of the text is compared. A post's opening is what identifies it; comparing the whole
    /// body would make an edited-on-the-network post (LinkedIn allows editing) stop matching its own
    /// node, which is precisely the case that most needs matching.
    /// </summary>
    public const int ComparedLength = 160;

    /// <summary>
    /// Text reduced to what survives a round trip through a network: whitespace collapsed, trimmed,
    /// truncated to <see cref="ComparedLength"/>. NOT lower-cased — case is signal in a post's
    /// opening line, and folding it only widens the chance of a false match.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var collapsed = string.Join(' ', text.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return collapsed.Length <= ComparedLength ? collapsed : collapsed[..ComparedLength];
    }

    /// <summary>
    /// The one candidate whose text matches <paramref name="postText"/>, or null when zero or
    /// several do.
    ///
    /// <para>Ambiguity resolves to NOTHING, never to "the first one". A person who posts a recurring
    /// format — a weekly digest opening the same way — would otherwise have every edition of it
    /// collapsed onto whichever the API happened to return first, and the resulting stats would be
    /// silently wrong rather than absent.</para>
    /// </summary>
    /// <param name="postText">The mesh post's text.</param>
    /// <param name="candidates">Posts pulled from the network.</param>
    public static PastPost? UniqueMatch(string? postText, IEnumerable<PastPost> candidates)
    {
        var needle = Normalize(postText);
        if (needle.Length == 0)
            return null;
        var hits = candidates
            .Where(c => string.Equals(Normalize(c.Text), needle, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>
    /// The content changes to apply to a mesh post from its matched live post, or an EMPTY map when
    /// there is nothing to say.
    ///
    /// <para>🚨 <b>An existing network id is never overwritten.</b> Once a node names its live post,
    /// that binding is a fact the mesh recorded at publish time; re-deriving it from a text match
    /// every night is how a correct binding gets replaced by a plausible one. The same applies to
    /// <c>publishedAt</c>: the network's timestamp fills a GAP, it does not correct the record.</para>
    ///
    /// <para>Returning empty when nothing changed is what keeps the nightly job from writing to every
    /// post it looks at — a no-op write still bumps the node's version, churns the change feed, and
    /// makes "what actually changed last night" unanswerable.</para>
    /// </summary>
    /// <param name="existingUrn">The post's current network id, if any.</param>
    /// <param name="existingPublishedAt">The post's current publication instant, if any.</param>
    /// <param name="matched">The live post it was matched to.</param>
    public static IReadOnlyDictionary<string, object?> UpdatesFor(
        string? existingUrn, DateTime? existingPublishedAt, PastPost matched)
    {
        var updates = new Dictionary<string, object?>();

        if (string.IsNullOrWhiteSpace(existingUrn))
        {
            updates["publishedUrn"] = matched.Urn;
            if (!string.IsNullOrWhiteSpace(matched.PostUrl))
                updates["publishedUrl"] = matched.PostUrl;
        }

        if (existingPublishedAt is null)
            updates["publishedAt"] = matched.PublishedAt.UtcDateTime;

        return updates;
    }
}
