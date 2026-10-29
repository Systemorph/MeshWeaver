namespace MeshWeaver.Mesh;

/// <summary>
/// The leading key of free-text search ranking (Systemorph/MeshWeaver#7381): <b>an exact name
/// match and a Space rank first</b>, ahead of every fuzzy or partial match. Both query executors
/// read this one rule — the in-memory <c>QueryEvaluator</c> path in core and the Postgres
/// generator in MeshWeaver.Plugins — so the two cannot disagree about which row leads
/// (Doc/Architecture/QueryProviderParity).
///
/// <para><b>The tier.</b> <see cref="ExactNameTier"/> when the node's <c>Name</c> or <c>Id</c>
/// equals the whole typed text (case-insensitive, surrounding whitespace ignored), plus
/// <see cref="SpaceTier"/> when the node is a <see cref="SpaceNodeType"/>. So an exactly named
/// Space (3) leads an exactly named page (2), which leads a Space that merely contains the words
/// (1), which leads everything else (0). Within a tier each executor keeps its own relevance score.
/// A query with no free text has tier 0 everywhere, so its order is untouched.</para>
///
/// <para><b>The boost.</b> A provider that reports one relevance number per row adds
/// <see cref="Boost(MeshNode?, string?)"/> to it: <see cref="TierWeight"/> is far above any score
/// an executor produces, so the sum orders by tier first and by the original score inside a tier,
/// wherever that number travels (a provider's own clip, a fan-out merge, the aggregator).</para>
/// </summary>
public static class SearchRanking
{
    /// <summary>The node type whose matches rank first (the mesh's top-level workspaces).</summary>
    public const string SpaceNodeType = "Space";

    /// <summary>Tier contribution of a row whose name or id equals the typed text.</summary>
    public const int ExactNameTier = 2;

    /// <summary>Tier contribution of a <see cref="SpaceNodeType"/> row.</summary>
    public const int SpaceTier = 1;

    /// <summary>
    /// The score one tier is worth. Larger than any POSITIVE relevance score an executor computes
    /// (fuzzy scores are in the low thousands, the Postgres ladder below 1 100). The in-memory fuzzy
    /// score has no lower bound — it subtracts a share of the text length — so a sort or clip that
    /// must be tier-first sorts by <see cref="Tier(object?, string?)"/> explicitly rather than
    /// relying on the boosted sum alone.
    /// </summary>
    public const double TierWeight = 1_000_000d;

    /// <summary>
    /// The ranking tier of a row against the typed free text: 0 when there is no text.
    /// </summary>
    /// <param name="name">The row's <c>Name</c>.</param>
    /// <param name="id">The row's <c>Id</c>.</param>
    /// <param name="nodeType">The row's <c>NodeType</c>.</param>
    /// <param name="textSearch">The query's free-text term (<c>ParsedQuery.TextSearch</c>).</param>
    /// <returns>The sum of <see cref="ExactNameTier"/> and <see cref="SpaceTier"/> that apply.</returns>
    public static int Tier(string? name, string? id, string? nodeType, string? textSearch)
    {
        var text = textSearch?.Trim();
        if (string.IsNullOrEmpty(text))
            return 0;
        var tier = 0;
        if (string.Equals(name?.Trim(), text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, text, StringComparison.OrdinalIgnoreCase))
            tier += ExactNameTier;
        if (string.Equals(nodeType, SpaceNodeType, StringComparison.Ordinal))
            tier += SpaceTier;
        return tier;
    }

    /// <summary>The ranking tier of a node; 0 for anything that is not a <see cref="MeshNode"/>.</summary>
    /// <param name="item">The row.</param>
    /// <param name="textSearch">The query's free-text term.</param>
    /// <returns>The node's tier, see <see cref="Tier(string?, string?, string?, string?)"/>.</returns>
    public static int Tier(object? item, string? textSearch)
        => item is MeshNode node ? Tier(node.Name, node.Id, node.NodeType, textSearch) : 0;

    /// <summary>The amount a provider adds to a row's relevance score: <c>tier × TierWeight</c>.</summary>
    /// <param name="node">The row.</param>
    /// <param name="textSearch">The query's free-text term.</param>
    /// <returns>The boost; 0 when the row has tier 0.</returns>
    public static double Boost(MeshNode? node, string? textSearch)
        => Tier(node, textSearch) * TierWeight;
}
