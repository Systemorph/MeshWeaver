using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#6581 — a negated filter is the COMPLEMENT within the same scope, on the
/// in-memory executor. The cases are <see cref="NegationCorpus"/>, which the Postgres executor in
/// MeshWeaver.Plugins runs too.
/// </summary>
public class NegatedFilterIsTheComplementTest
{
    private static readonly QueryParser Parser = new();
    private static readonly QueryEvaluator Evaluator = new();

    private static IReadOnlyList<MeshNode> Nodes() =>
        NegationCorpus.Nodes
            .Select(n => new MeshNode(n.Id, "NegationScope")
            {
                Name = n.Name,
                NodeType = NegationCorpus.NodeType,
                Content = n.ContentJson is null ? null : JsonSerializer.Deserialize<JsonElement>(n.ContentJson),
            })
            .ToList();

    private static string[] Run(string filter) =>
        Nodes().Where(n => Evaluator.Matches(n, Parser.Parse(filter))).Select(n => n.Id).ToArray();

    /// <summary>A set's canonical spelling, so two sets compare as one string.</summary>
    private static string Set(IEnumerable<string> ids) => string.Join(",", ids.OrderBy(i => i, StringComparer.Ordinal));

    public static TheoryData<string> Filters()
    {
        var data = new TheoryData<string>();
        foreach (var c in NegationCorpus.Cases)
            data.Add(c.Filter);
        return data;
    }

    [Theory]
    [MemberData(nameof(Filters))]
    public void EveryCorpusCaseReturnsExactlyItsExpectedSet(string filter)
    {
        var expected = NegationCorpus.Cases.Single(c => c.Filter == filter);
        Set(Run(filter)).Should().Be(Set(expected.Expected), $"`{filter}`: {expected.Note}");
    }

    [Fact]
    public void EachNegationPartitionsTheNodeSetWithItsPositiveTwin()
    {
        var all = NegationCorpus.Nodes.Select(n => n.Id).ToArray();
        foreach (var (positive, negated) in NegationCorpus.Complements)
        {
            var pos = Run(positive);
            var neg = Run(negated);
            pos.Should().NotBeEmpty($"the positive `{positive}` must discriminate, or the complement proves nothing");
            pos.Length.Should().BeLessThan(all.Length, $"the positive `{positive}` must not match everything");
            Set(pos.Intersect(neg)).Should().Be("", $"`{negated}` must exclude what `{positive}` matches");
            Set(pos.Concat(neg)).Should().Be(Set(all), $"`{positive}` and `{negated}` together cover the scope");
        }
    }

    [Fact]
    public void NegatedWildcardKeepsItsPatternAsANotEqual()
    {
        var condition = Parser.Parse("-content.apiKey:enc*").Filter.Should()
            .BeOfType<QueryComparison>().Subject.Condition;
        condition.Operator.Should().Be(QueryOperator.NotEqual);
        condition.Value.Should().Be("enc*", "every executor reads the `*` as the complement of the glob");
    }

    [Theory]
    [InlineData("-namespace:Acme/Secret", "Acme/Secret")]
    [InlineData("-namespace:*/_Thread", "*/_Thread")]
    public void NegatedNamespaceIsAFilterNeverAnAnchor(string query, string value)
    {
        var parsed = Parser.Parse(query + " partitions:all");
        parsed.Path.Should().BeNullOrEmpty("an exclusion can never pin the query to what it excludes");
        var condition = parsed.Filter.Should().BeOfType<QueryComparison>().Subject.Condition;
        condition.Selector.Should().Be("namespace");
        condition.Operator.Should().Be(QueryOperator.NotEqual);
        condition.Value.Should().Be(value);

        var inside = new MeshNode("x", "Acme/Secret") { Name = "x" };
        var thread = new MeshNode("t", "Acme/_Thread") { Name = "t" };
        var elsewhere = new MeshNode("y", "Acme/Open") { Name = "y" };
        var excluded = value == "Acme/Secret" ? inside : thread;
        Evaluator.Matches(excluded, parsed).Should().BeFalse($"`{query}` must exclude {excluded.Namespace}");
        Evaluator.Matches(elsewhere, parsed).Should().BeTrue($"`{query}` must keep {elsewhere.Namespace}");
    }

    /// <summary>
    /// A SCOPED wildcard namespace: the positive widens to self-or-below
    /// (<c>namespace LIKE '*/_Thread' OR namespace LIKE '*/_Thread/*'</c>), so its negation must be
    /// the AND of the two negated patterns — a node BELOW a thread namespace is matched by the
    /// positive and must therefore be excluded by the negation. Unscoped, the pair is the plain
    /// one-pattern complement (negative control: the below-node is outside BOTH).
    /// </summary>
    [Theory]
    [InlineData("scope:subtree", true)]
    [InlineData("scope:descendants", true)]
    [InlineData("", false)]
    public void ANegatedScopedWildcardNamespaceIsTheComplementOfItsPositive(string scope, bool widened)
    {
        MeshNode[] nodes =
        [
            new("t", "Acme/_Thread") { Name = "t" },
            new("below", "Acme/_Thread/topic") { Name = "below" },
            new("open", "Acme/Open") { Name = "open" },
            new("root", "") { Name = "root" },
        ];
        var positive = Parser.Parse($"namespace:*/_Thread {scope} partitions:all");
        var negated = Parser.Parse($"-namespace:*/_Thread {scope} partitions:all");
        var pos = nodes.Where(n => Evaluator.Matches(n, positive)).Select(n => n.Id).ToArray();
        var neg = nodes.Where(n => Evaluator.Matches(n, negated)).Select(n => n.Id).ToArray();

        Set(pos).Should().Be(widened ? "below,t" : "t", $"the positive under `{scope}`");
        Set(pos.Intersect(neg)).Should().Be("", $"`-namespace:*/_Thread {scope}` must exclude what the positive matches");
        Set(pos.Concat(neg)).Should().Be(Set(nodes.Select(n => n.Id)), "together they cover every node");
    }

    /// <summary>The public pair is complementary for every accepted input — a wildcard-free pattern
    /// (contains semantics) included.</summary>
    [Theory]
    [InlineData("foobar", "foo")]
    [InlineData("barbaz", "foo")]
    [InlineData("foobar", "foo*")]
    [InlineData("barfoo", "foo*")]
    public void IsNotLikeMatchIsTheComplementOfIsLikeMatch(string value, string pattern) =>
        QueryWildcard.IsNotLikeMatch(value, pattern).Should().Be(!QueryWildcard.IsLikeMatch(value, pattern),
            $"`{value}` against `{pattern}`");
}
