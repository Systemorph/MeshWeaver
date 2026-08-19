using System;
using System.Collections.Generic;
using MeshWeaver.Social;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The matching rules behind the nightly history sync.
///
/// <para>🚨 These are pinned hard because a WRONG match is silent and permanent-feeling: attaching
/// the wrong network id to a node makes every later stat lookup read someone else's numbers, and the
/// counts look perfectly plausible. Absent stats are obvious; wrong ones are not.</para>
/// </summary>
public class SocialHistoryMatchTest
{
    private static PastPost Live(string urn, string text, DateTimeOffset? at = null) =>
        new(urn, $"https://www.linkedin.com/feed/update/{urn}/", text, [],
            at ?? new DateTimeOffset(2026, 8, 17, 6, 0, 0, TimeSpan.Zero), null);

    /// <summary>A network round trip reflows whitespace; the same post must still match itself.</summary>
    [Fact]
    public void Normalize_CollapsesWhitespace_SoAReflowedPostStillMatches()
    {
        Assert.Equal(
            SocialHistoryMatch.Normalize("I've been a Microsoft partner for 15 years."),
            SocialHistoryMatch.Normalize("I've been a Microsoft   partner\n\nfor 15 years.  "));
    }

    /// <summary>Case is NOT folded — it is signal in an opening line, and folding only widens the
    /// chance of a false match.</summary>
    [Fact]
    public void Normalize_KeepsCase()
    {
        Assert.NotEqual(
            SocialHistoryMatch.Normalize("Token economics will die"),
            SocialHistoryMatch.Normalize("token economics will die"));
    }

    /// <summary>The ordinary case: one live post opens the same way, so it is the match.</summary>
    [Fact]
    public void UniqueMatch_FindsTheOnePostThatOpensTheSameWay()
    {
        var candidates = new[]
        {
            Live("urn:li:share:1", "Token economics will die. Everyone is arguing about cost per token."),
            Live("urn:li:share:2", "I've been a Microsoft partner for 15 years. Last week I needed a deck."),
        };
        Assert.Equal("urn:li:share:2",
            SocialHistoryMatch.UniqueMatch(
                "I've been a Microsoft partner for 15 years. Last week I needed a deck.", candidates)?.Urn);
    }

    /// <summary>
    /// 🚨 Two candidates opening identically resolve to NOTHING, never to "the first one". Someone
    /// posting a recurring format — a weekly digest with the same opening — would otherwise have
    /// every edition collapsed onto whichever the API happened to return first, and the stats would
    /// be silently wrong rather than absent.
    /// </summary>
    [Fact]
    public void UniqueMatch_RefusesWhenTwoCandidatesAreIndistinguishable()
    {
        var same = "Weekly digest — what shipped this week:";
        Assert.Null(SocialHistoryMatch.UniqueMatch(same, [Live("urn:li:share:1", same), Live("urn:li:share:2", same)]));
    }

    /// <summary>No candidate, or nothing to compare, means leave it alone.</summary>
    [Fact]
    public void UniqueMatch_RefusesOnNoCandidateOrEmptyText()
    {
        Assert.Null(SocialHistoryMatch.UniqueMatch("something", [Live("urn:li:share:1", "different")]));
        Assert.Null(SocialHistoryMatch.UniqueMatch("", [Live("urn:li:share:1", "")]));
        Assert.Null(SocialHistoryMatch.UniqueMatch("   ", [Live("urn:li:share:1", "anything")]));
    }

    /// <summary>A post with no network id gets one, plus its URL and publication instant.</summary>
    [Fact]
    public void UpdatesFor_FillsTheGapOnAnUnlinkedPost()
    {
        var updates = SocialHistoryMatch.UpdatesFor(null, null, Live("urn:li:share:42", "t"));
        Assert.Equal("urn:li:share:42", updates["publishedUrn"]);
        Assert.Equal("https://www.linkedin.com/feed/update/urn:li:share:42/", updates["publishedUrl"]);
        Assert.Equal(new DateTime(2026, 8, 17, 6, 0, 0, DateTimeKind.Utc), updates["publishedAt"]);
    }

    /// <summary>
    /// 🚨 An existing network id is never overwritten. It is a fact the mesh recorded at publish
    /// time; re-deriving it from a text match every night is how a CORRECT binding gets replaced by a
    /// merely plausible one.
    /// </summary>
    [Fact]
    public void UpdatesFor_NeverOverwritesAnExistingUrnOrTimestamp()
    {
        var already = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
        var updates = SocialHistoryMatch.UpdatesFor("urn:li:share:ORIGINAL", already, Live("urn:li:share:OTHER", "t"));
        Assert.Empty(updates);
    }

    /// <summary>Nothing to change ⇒ no write. A no-op write still bumps the node's version and churns
    /// the change feed, which makes "what actually changed last night" unanswerable.</summary>
    [Fact]
    public void UpdatesFor_IsEmptyWhenThereIsNothingToSay()
    {
        Assert.Empty(SocialHistoryMatch.UpdatesFor("urn:li:share:1", DateTime.UtcNow, Live("urn:li:share:1", "t")));
    }
}
