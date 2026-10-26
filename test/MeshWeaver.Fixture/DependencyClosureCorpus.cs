using System.Collections.Immutable;

namespace MeshWeaver.Fixture;

/// <summary>One read of the <see cref="DependencyClosureCorpus"/> and its exact answer.</summary>
/// <param name="Edges">The forward edges — (source, target): source depends on target.</param>
/// <param name="Direction"><c>dependents</c> or <c>dependencies</c>.</param>
/// <param name="Start">The node asked about.</param>
/// <param name="MaxDepth">The depth bound, or null for all.</param>
/// <param name="Expected">The exact rows, in order, each <c>node|depth|a&gt;b&gt;c</c> (path from the start).</param>
/// <param name="Note">Why this case is in the corpus.</param>
public sealed record DependencyClosureCase(
    ImmutableArray<(string Source, string Target)> Edges,
    string Direction,
    string Start,
    int? MaxDepth,
    ImmutableArray<string> Expected,
    string Note);

/// <summary>
/// 🔴 <b>The dependency closure has ONE answer — in memory and on PostgreSQL — membership, depth AND
/// path</b> (core <c>Doc/Architecture/DependencyGraph</c>, Query Provider Parity).
///
/// <para>Pure data in a project both repositories reference, like <see cref="NegationCorpus"/>:
/// core runs every case through <c>MeshWeaver.Mesh.DependencyClosure</c>; MeshWeaver.Plugins inserts
/// the same edges into <c>public.dependency_edge</c> and reads <c>public.dependents</c> /
/// <c>public.dependencies</c>. The rule both follow: shortest chain; among equally short chains the
/// next hop that is smallest under ordinal comparison; no self-pair.</para>
/// </summary>
public static class DependencyClosureCorpus
{
    /// <summary>The cycle fixture: a → b → c → a, plus a leaf d → a.</summary>
    public static readonly ImmutableArray<(string Source, string Target)> Cycle =
        [("a", "b"), ("b", "c"), ("c", "a"), ("d", "a")];

    /// <summary>A diamond with a tail: u → x → {y, z} → w → v, two equally short chains to w.</summary>
    public static readonly ImmutableArray<(string Source, string Target)> Diamond =
        [("x", "y"), ("x", "z"), ("y", "w"), ("z", "w"), ("w", "v"), ("u", "x")];

    /// <summary>Two crossing shortest chains s → a → z → t and s → b → y → t: the next hop is chosen
    /// on the DEPENDENT side (s steps to a, the smaller of a and b), so the path to t runs through z
    /// although y sorts before z — the case that tells this rule from a sorted reverse traversal.</summary>
    public static readonly ImmutableArray<(string Source, string Target)> Crossing =
        [("s", "a"), ("a", "z"), ("z", "t"), ("s", "b"), ("b", "y"), ("y", "t")];

    /// <summary>Every case.</summary>
    public static readonly ImmutableArray<DependencyClosureCase> Cases =
    [
        new(Cycle, "dependents", "a", null, ["c|1|a>c", "d|1|a>d", "b|2|a>c>b"],
            "inside a cycle every other member is a dependent, at its shortest depth, never the start itself"),
        new(Cycle, "dependencies", "a", null, ["b|1|a>b", "c|2|a>b>c"],
            "the same cycle read forward"),
        new(Cycle, "dependencies", "d", null, ["a|1|d>a", "b|2|d>a>b", "c|3|d>a>b>c"],
            "a leaf outside the cycle reaches all of it, transitively"),
        new(Cycle, "dependents", "d", null, [],
            "nothing depends on the leaf — an EMPTY answer, which is an answer"),
        new(Cycle, "dependents", "a", 1, ["c|1|a>c", "d|1|a>d"],
            "the depth bound cuts the transitive row"),
        new(Diamond, "dependents", "w", null, ["y|1|w>y", "z|1|w>z", "x|2|w>y>x", "u|3|w>y>x>u"],
            "two equally short chains: the smaller next hop (y) is the path"),
        new(Diamond, "dependencies", "u", null, ["x|1|u>x", "y|2|u>x>y", "z|2|u>x>z", "w|3|u>x>y>w", "v|4|u>x>y>w>v"],
            "the same tie read forward"),
        new(Crossing, "dependents", "t", null, ["y|1|t>y", "z|1|t>z", "a|2|t>z>a", "b|2|t>y>b", "s|3|t>z>a>s"],
            "the next hop is the dependent's: s → a (a < b), so s's chain runs through z although y < z"),
        new(Crossing, "dependencies", "s", null, ["a|1|s>a", "b|1|s>b", "y|2|s>b>y", "z|2|s>a>z", "t|3|s>a>z>t"],
            "the same crossing read forward"),
    ];
}
