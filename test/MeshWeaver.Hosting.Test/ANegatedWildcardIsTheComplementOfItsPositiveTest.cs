using System.Text.Json.Nodes;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins Systemorph/MeshWeaver#6581: a NEGATED term is the complement of its positive inside the
/// same scope. <c>-content.apiKey:enc*</c> is exactly the nodes <c>content.apiKey:enc*</c> does not
/// match — a node that LACKS the field included, as the null-inclusive <c>NotEqual</c> and
/// <c>NotIn</c> already are — and a negated namespace is never read as the positive one.
///
/// <para>The parser turned a <c>*</c> into <c>Like</c> only for a term that was NOT negated, so a
/// negated wildcard travelled as <c>NotEqual</c> against the literal text <c>enc*</c>, which no
/// value equals: the in-memory evaluator (and the SQL generators, which read the same AST)
/// answered EVERY node. The namespace branch of the reserved qualifiers never read the operator at
/// all, so <c>-namespace:*/X</c> became the positive wildcard filter and <c>-namespace:X</c> the
/// positive path anchor.</para>
///
/// <para>The new operator is asserted by its NAME on purpose: this file compiles against the
/// language as it was before the fix, so the test-only commit fails by assertion, not by a compile
/// error.</para>
/// </summary>
public class ANegatedWildcardIsTheComplementOfItsPositiveTest
{
    private static ParsedQuery Parse(string query) => new QueryParser().Parse(query);

    private static string[] Matching(IEnumerable<MeshNode> nodes, string query, Func<MeshNode, string> label)
    {
        var parsed = Parse(query);
        var evaluator = new QueryEvaluator();
        return nodes.Where(n => evaluator.Matches(n, parsed)).Select(label).ToArray();
    }

    private static IEnumerable<QueryCondition> Conditions(QueryNode? node) => node switch
    {
        QueryComparison c => [c.Condition],
        QueryAnd a => a.Children.SelectMany(Conditions),
        QueryOr o => o.Children.SelectMany(Conditions),
        _ => []
    };

    private static MeshNode KeyNode(string id, string? apiKey) => new(id, "Store")
    {
        Content = apiKey is null
            ? new JsonObject { ["other"] = "x" }
            : new JsonObject { ["apiKey"] = apiKey },
    };

    /// <summary>Two nodes whose apiKey starts with enc, one that does not, one with no apiKey at all.</summary>
    private static MeshNode[] KeyNodes() =>
    [
        KeyNode("enc1", "enc1-secret"),
        KeyNode("enc2", "enc2-secret"),
        KeyNode("plain", "plain-text"),
        KeyNode("absent", null),
    ];

    private static MeshNode[] NamedNodes() =>
    [
        new("foo", "Store") { Name = "foo" },
        new("foobar", "Store") { Name = "foobar" },
        new("bar", "Store") { Name = "bar" },
        new("nameless", "Store"),
    ];

    private static MeshNode[] NamespaceNodes() =>
    [
        new("n", "A/X"),
        new("n", "A/X/Y"),
        new("n", "A/XY"),
        new("n", "A/Z"),
        new("n", "B"),
    ];

    [Fact]
    public void AParsedNegatedWildcardIsTheNegatedLike()
    {
        var condition = Conditions(Parse("-content.apiKey:enc*").Filter)
            .Should().ContainSingle().Subject;

        condition.Selector.Should().Be("content.apiKey");
        condition.Operator.ToString().Should().Be("NotLike",
            "a negated wildcard is the complement of Like, never a NotEqual against the literal text 'enc*'");
        condition.Values.Should().Equal("enc*");
    }

    [Fact]
    public void ANegatedWildcardMatchesTheComplementOfItsPositive()
    {
        var nodes = KeyNodes();

        Matching(nodes, "content.apiKey:enc*", n => n.Id)
            .Should().Equal("enc1", "enc2");
        Matching(nodes, "-content.apiKey:enc*", n => n.Id)
            .Should().Equal(new[] { "plain", "absent" },
                "the complement keeps every node the positive does not match, the one lacking the field included");
    }

    [Fact]
    public void ANegatedBareWildcardLeavesOnlyTheNodesThatLackTheField()
    {
        var nodes = KeyNodes();

        Matching(nodes, "content.apiKey:*", n => n.Id)
            .Should().Equal("enc1", "enc2", "plain");
        Matching(nodes, "-content.apiKey:*", n => n.Id)
            .Should().Equal(new[] { "absent" }, "only a node without the field is outside `field:*`");
    }

    [Fact]
    public void ANegatedWildcardOnANodeFieldIsTheComplementToo()
    {
        var nodes = NamedNodes();

        Matching(nodes, "name:foo*", n => n.Id)
            .Should().Equal("foo", "foobar");
        Matching(nodes, "-name:foo*", n => n.Id)
            .Should().Equal("bar", "nameless");
    }

    /// <summary>The controls the fix must not move: a negated LITERAL stays NotEqual and null-inclusive.</summary>
    [Fact]
    public void ANegatedLiteralKeepsItsNullInclusiveMeaning()
    {
        Matching(KeyNodes(), "-content.apiKey:plain-text", n => n.Id)
            .Should().Equal("enc1", "enc2", "absent");
        Matching(NamedNodes(), "-name:foo", n => n.Id)
            .Should().Equal("foobar", "bar", "nameless");

        Conditions(Parse("-content.apiKey:plain-text").Filter)
            .Should().ContainSingle().Subject.Operator.Should().Be(QueryOperator.NotEqual);
        Conditions(Parse("-name:foo").Filter)
            .Should().ContainSingle().Subject.Operator.Should().Be(QueryOperator.NotEqual);
    }

    /// <summary>
    /// A negated namespace says where NOT to look, which never says where TO look — the same rule
    /// as a negated path (#4296): it is a FILTER, never an anchor, and the query stays unanchored.
    /// </summary>
    [Theory]
    [InlineData("-namespace:*/X nodeType:Foo")]
    [InlineData("-namespace:*/X scope:subtree nodeType:Foo")]
    [InlineData("-namespace:A/X nodeType:Foo")]
    [InlineData("-namespace:A/X scope:descendants nodeType:Foo")]
    public void ANegatedNamespaceIsNeverAnAnchor(string query)
    {
        var parsed = Parse(query);

        parsed.Path.Should().BeNull($"'{query}' excludes a namespace, it names no base path to walk from");
        parsed.Paths.Should().BeNull();
        parsed.IsSufficientlySpecified().Should().BeFalse(
            $"'{query}' says where NOT to look — it needs an anchor or {ParsedQuery.CrossPartitionQualifier}");
    }

    /// <summary>The other half: the exclusion must still be APPLIED, as a negated namespace filter.</summary>
    [Theory]
    [InlineData("-namespace:*/X nodeType:Foo", "NotLike", "*/X")]
    [InlineData("-namespace:A/X nodeType:Foo", "NotEqual", "A/X")]
    public void ANegatedNamespaceSurvivesAsANegatedFilter(string query, string expectedOperator, string expectedValue)
    {
        var condition = Conditions(Parse(query).Filter)
            .Where(c => c.Selector.Equals("namespace", StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle().Subject;

        condition.Operator.ToString().Should().Be(expectedOperator);
        condition.Values.Should().Equal(expectedValue);
    }

    /// <summary>
    /// The exclusion applies to exactly what the positive form selects. A subtree widening of a
    /// negation is an AND of two negations (De Morgan): an OR of them would exclude nothing.
    /// </summary>
    [Theory]
    [InlineData("namespace:*/X scope:subtree", "A/X|A/X/Y")]
    [InlineData("-namespace:*/X", "A/X/Y|A/XY|A/Z|B")]
    [InlineData("-namespace:*/X scope:subtree", "A/XY|A/Z|B")]
    [InlineData("-namespace:A/X", "A/X/Y|A/XY|A/Z|B")]
    [InlineData("-namespace:A/X scope:descendants", "A/XY|A/Z|B")]
    public void ANegatedNamespaceExcludesExactlyWhatItNames(string query, string expectedNamespaces)
    {
        Matching(NamespaceNodes(), query, n => n.Namespace!)
            .Should().Equal(expectedNamespaces.Split('|'), $"'{query}'");
    }
}