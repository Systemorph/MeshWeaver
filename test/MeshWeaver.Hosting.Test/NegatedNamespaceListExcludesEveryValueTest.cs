using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#6844 — a negated multi-value namespace list excludes EVERY listed value,
/// quoted or not. The list parser matched the separator <c>OR</c> without a word boundary, so a
/// value that merely starts with those letters lost them: <c>-namespace:(acme/a OR orders)</c>
/// became <c>NotIn [acme/a, ders]</c> and <c>orders</c> was never excluded. Both executors receive
/// the parser's condition, so the defect was the same on each; the plain-field twins of these cases
/// run on Postgres too through <see cref="MeshWeaver.Fixture.NegationCorpus"/>.
/// </summary>
public class NegatedNamespaceListExcludesEveryValueTest
{
    private static readonly QueryParser Parser = new();
    private static readonly QueryEvaluator Evaluator = new();

    [Theory]
    [InlineData("-namespace:(\"acme/a\" OR \"acme/b\") partitions:all", "acme/a", "acme/b")]
    [InlineData("-namespace:(acme/a OR acme/b) partitions:all", "acme/a", "acme/b")]
    [InlineData("-namespace:(acme/a OR orders) partitions:all", "acme/a", "orders")]
    [InlineData("-namespace:(orders OR acme/a) partitions:all", "orders", "acme/a")]
    [InlineData("-namespace:(Oracle/x or acme/a) partitions:all", "Oracle/x", "acme/a")]
    [InlineData("-namespace:(\"acme/a\"OR\"orders\") partitions:all", "acme/a", "orders")]
    public void EveryListedNamespaceIsExcluded(string query, string first, string second)
    {
        var parsed = Parser.Parse(query);
        parsed.Path.Should().BeNullOrEmpty("an exclusion never anchors the query");
        var condition = parsed.Filter.Should().BeOfType<QueryComparison>().Subject.Condition;
        condition.Selector.Should().Be("namespace");
        condition.Operator.Should().Be(QueryOperator.NotIn);
        condition.Values.Should().Equal([first, second], "every listed value reaches the executors whole");

        Evaluator.Matches(Node(first), parsed).Should().BeFalse($"`{first}` is listed");
        Evaluator.Matches(Node(second), parsed).Should().BeFalse($"`{second}` is listed");
        Evaluator.Matches(Node("other/ns"), parsed).Should().BeTrue("a namespace the list does not name is kept");
    }

    [Fact]
    public void AnAnchoredQueryExcludesEveryListedSubNamespace()
    {
        var parsed = Parser.Parse("namespace:acme scope:descendants -namespace:(\"acme/a\" OR \"acme/orders\")");
        parsed.Path.Should().Be("acme", "the exclusion leaves the anchor alone");
        Evaluator.Matches(Node("acme/a"), parsed).Should().BeFalse();
        Evaluator.Matches(Node("acme/orders"), parsed).Should().BeFalse();
        Evaluator.Matches(Node("acme/c"), parsed).Should().BeTrue();
    }

    /// <summary>Negative control: a standalone <c>OR</c> is still the separator, in any case.</summary>
    [Theory]
    [InlineData("namespace:(acme/a OR acme/b)")]
    [InlineData("namespace:(acme/a or acme/b)")]
    [InlineData("namespace:(\"acme/a\" OR \"acme/b\")")]
    public void AStandaloneOrIsStillTheSeparator(string query)
    {
        var condition = Parser.Parse(query).Filter.Should().BeOfType<QueryComparison>().Subject.Condition;
        condition.Operator.Should().Be(QueryOperator.In);
        condition.Values.Should().Equal("acme/a", "acme/b");
    }

    private static MeshNode Node(string ns) => new("x", ns) { Name = "x" };
}
