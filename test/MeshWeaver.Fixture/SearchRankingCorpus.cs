using System.Collections.Immutable;

namespace MeshWeaver.Fixture;

/// <summary>One node of the <see cref="SearchRankingCorpus"/> fixture set.</summary>
/// <param name="Id">The node id (its path is <c>{Namespace}/{Id}</c> wherever a suite seeds it).</param>
/// <param name="Name">The node's own <c>Name</c>.</param>
/// <param name="NodeType">The node's <c>NodeType</c> — <c>Space</c> for the rows the tier lifts.</param>
/// <param name="Text">A content string the free text can match in (the "fuzzy content match"), or <see langword="null"/>.</param>
/// <param name="MinutesAfterEpoch">The node's <c>LastModified</c>, as minutes after <see cref="SearchRankingCorpus.Epoch"/>.</param>
public sealed record SearchRankingNode(string Id, string Name, string NodeType, string? Text, int MinutesAfterEpoch);

/// <summary>One free-text query and the ids that must LEAD its result, in this order.</summary>
/// <param name="Text">The typed free text (the query adds only <see cref="SearchRankingCorpus.Namespace"/>'s anchor).</param>
/// <param name="Leading">The ids that must be the first rows, in exactly this order.</param>
/// <param name="Below">Ids that must ALSO be in the result, somewhere after every <paramref name="Leading"/> id.</param>
/// <param name="Note">Why this case is in the corpus.</param>
public sealed record SearchRankingCase(string Text, ImmutableArray<string> Leading, ImmutableArray<string> Below, string Note);

/// <summary>
/// 🔴 <b>Exact-name and Space matches rank first — on BOTH query executors
/// (Systemorph/MeshWeaver#7381).</b>
///
/// <para>A reader looking for a Space typed its name and got slides, PDFs and pages that merely
/// contained the word, ahead of — or instead of — the Space. The rule is
/// <c>MeshWeaver.Mesh.SearchRanking</c>: the leading sort key of a free-text query is a tier (exact
/// name or id = 2, Space = 1, summed), and each executor's own relevance score orders within it.</para>
///
/// <para>Like <see cref="NegationCorpus"/> this is pure data in a project both repositories reference:
/// core seeds the nodes into the in-memory store and runs every case through <c>MeshQuery</c>;
/// MeshWeaver.Plugins seeds the same nodes into Postgres and runs every case through its query
/// provider. Every <see cref="SearchRankingCase.Leading"/> list is chosen so that it holds under the
/// tier and NOT under either executor's score alone — so an executor that drops the tier fails.</para>
///
/// <para><b>The negative control</b> is <see cref="UnrelatedText"/>: no row it matches is a Space or an
/// exact name, so every tier is 0 and the executor's order must be exactly what it was without the
/// rule. Each suite asserts that against its own executor's tier-free ranking.</para>
/// </summary>
public static class SearchRankingCorpus
{
    /// <summary>The namespace every fixture node lives in, and the anchor every case runs under.</summary>
    public const string Namespace = "RankProbe";

    /// <summary>The origin of <see cref="SearchRankingNode.MinutesAfterEpoch"/>.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The fixture set.</summary>
    public static readonly ImmutableArray<SearchRankingNode> Nodes =
    [
        // ── "Compliance": a Space, the pages and PDFs that crowded it out. ──
        // The Space's id does not carry the word and its text is long, so a score that reads the path and
        // penalises length (the in-memory fuzzy score) ranks it BELOW the page of the same name.
        new("policies-and-audits-workspace", "Compliance", "Space", "Policies, audits, certifications and the regulator correspondence of every entity", 1),
        // The word at the very start of the path and the name: the strongest fuzzy hit there is.
        new("Compliance-Scan", "Compliance scan.pdf", "Markdown", null, 9),
        // Only the content mentions it — the "fuzzy content match".
        new("weekly-notes", "Weekly notes", "Markdown", "We discussed compliance with the auditor", 8),
        // A page with the SAME exact name as the Space, written later: the tier's Space half decides.
        new("compliance", "Compliance", "Markdown", null, 7),

        // ── "Budget": an exact name against partial ones. ──
        // The exact match is the OLDEST row and its id does not carry the word.
        new("q3-plan", "Budget", "Markdown", null, 2),
        new("budget-draft", "Budget draft", "Markdown", null, 6),
        new("budget-review", "Budget review", "Markdown", null, 5),

        // ── The negative control: neither a Space nor an exact name among the matches. ──
        new("renewal-contoso", "Contoso renewal", "Markdown", "renewal terms", 3),
        new("renewal-fabrikam", "Fabrikam renewal", "Markdown", null, 4),
        new("terms-wingtip", "Wingtip terms", "Markdown", "renewal pending", 10),
    ];

    private static ImmutableArray<string> Ids(params string[] ids) => [.. ids];

    /// <summary>The ranking cases.</summary>
    public static readonly ImmutableArray<SearchRankingCase> Cases =
    [
        new("Compliance", Ids("policies-and-audits-workspace", "compliance"), Ids("Compliance-Scan", "weekly-notes"),
            "An exactly named Space (tier 3) leads an exactly named page (tier 2), and both lead the "
            + "fuzzy content and partial-name matches (tier 0)."),
        new("compliance", Ids("policies-and-audits-workspace", "compliance"), Ids("Compliance-Scan", "weekly-notes"),
            "Case-insensitive: the typed spelling does not change the tier."),
        new("Budget", Ids("q3-plan"), Ids("budget-draft", "budget-review"),
            "An exact node name (tier 2) leads the partial ones, although it is the oldest row and its "
            + "id does not carry the word."),
    ];

    /// <summary>
    /// The negative control's free text: every row it matches has tier 0, so its order must be
    /// exactly the executor's tier-free ranking.
    /// </summary>
    public const string UnrelatedText = "renewal";

    /// <summary>The ids <see cref="UnrelatedText"/> matches (as a set; the ORDER is the executor's own).</summary>
    public static readonly ImmutableArray<string> UnrelatedMatches =
        Ids("renewal-contoso", "renewal-fabrikam", "terms-wingtip");
}
