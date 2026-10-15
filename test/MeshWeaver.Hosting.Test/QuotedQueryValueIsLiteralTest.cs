using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#6634 — a QUOTED query value is literal: a <c>|</c> inside the quotes is not
/// alternation and a <c>*</c> inside the quotes is not a wildcard, so a caller can query any exact
/// namespace or path. Alternation BETWEEN quoted values (<c>path:"a b"|"c"</c>, the shape the path
/// resolver builds) is unchanged — the bar is outside the quotes. The content-field cases (both
/// executors) live in <see cref="MeshWeaver.Fixture.NegationCorpus"/>.
/// <para>Negative control: each unquoted twin still parses as alternation / wildcard — so the
/// literal reading is the quoting, not a parser that stopped reading <c>|</c> and <c>*</c>.</para>
/// </summary>
public class QuotedQueryValueIsLiteralTest
{
    private static readonly QueryParser Parser = new();
    private static readonly QueryEvaluator Evaluator = new();

    [Theory]
    [InlineData("acme/Docs/a|b")]
    [InlineData("acme/Docs/report*")]
    [InlineData("acme/*/x|y*")]
    public void AQuotedNamespaceIsThatNamespace(string ns)
    {
        var parsed = Parser.Parse($"namespace:\"{ns}\"");
        parsed.Path.Should().Be(ns, "the whole quoted namespace is the query's anchor");
        parsed.Filter.Should().BeNull("no alternation filter and no LIKE pattern may come out of a quoted value");
        parsed.TextSearch.Should().BeNullOrEmpty();
        parsed.Scope.Should().Be(QueryScope.Children, "namespace:X lists X's children, as for an unquoted namespace");
    }

    [Fact]
    public void UnquotedNamespaceStillAlternatesAndGlobs()
    {
        var alternation = Parser.Parse("namespace:acme/a|acme/b").Filter.Should().BeOfType<QueryComparison>().Subject.Condition;
        alternation.Operator.Should().Be(QueryOperator.In);
        string.Join(",", alternation.Values).Should().Be("acme/a,acme/b");

        var wildcard = Parser.Parse("namespace:acme/report* partitions:all").Filter.Should().BeOfType<QueryComparison>().Subject.Condition;
        wildcard.Operator.Should().Be(QueryOperator.Like);
        wildcard.Value.Should().Be("acme/report*");
    }

    [Theory]
    [InlineData("acme/Docs/a|b")]
    [InlineData("acme/Docs/report*")]
    public void ANegatedQuotedNamespaceExcludesExactlyThatNamespace(string ns)
    {
        var parsed = Parser.Parse($"-namespace:\"{ns}\" partitions:all");
        parsed.Path.Should().BeNullOrEmpty("an exclusion never anchors the query");
        var condition = parsed.Filter.Should().BeOfType<QueryComparison>().Subject.Condition;
        condition.Selector.Should().Be("namespace");
        string.Join(",", condition.Values).Should().Be(ns);

        Evaluator.Matches(new MeshNode("x", ns) { Name = "x" }, parsed).Should().BeFalse($"`{ns}` itself is excluded");
        Evaluator.Matches(new MeshNode("y", "acme/Docs/a") { Name = "y" }, parsed).Should().BeTrue("a namespace a `|` alternation would name is kept");
        Evaluator.Matches(new MeshNode("z", "acme/Docs/report-2026") { Name = "z" }, parsed).Should().BeTrue("a namespace a `*` glob would match is kept");
    }

    [Theory]
    [InlineData("namespace:>\"acme/x*\" partitions:all", QueryOperator.GreaterThan)]
    [InlineData("namespace:<=\"acme/x*\" partitions:all", QueryOperator.LessOrEqual)]
    public void AQuotedOrderedNamespaceKeepsItsOperatorAndItsStar(string query, QueryOperator op)
    {
        var condition = Parser.Parse(query).Filter.Should().BeOfType<QueryComparison>().Subject.Condition;
        condition.Selector.Should().Be("namespace");
        condition.Operator.Should().Be(op, "a quoted ordered operand is literal: the operator survives, no Like");
        condition.Values.Should().Equal("acme/x*");
    }

    [Fact]
    public void AQuotedPathIsThatPath()
    {
        var parsed = Parser.Parse("path:\"acme/a|b\"");
        parsed.Path.Should().Be("acme/a|b");
        parsed.Paths.Should().BeNull("a quoted `|` is not a path alternation");
        parsed.Filter.Should().BeNull();
    }

    [Fact]
    public void AlternationBetweenQuotedValuesIsUnchanged()
    {
        var parsed = Parser.Parse("path:\"a/b c\"|\"a\"");
        string.Join(",", parsed.Paths ?? []).Should().Be("a/b c,a", "the bar OUTSIDE the quotes is still alternation (PathResolutionService)");
    }

    [Theory]
    [InlineData("nodeType:\"Acme/*\"", QueryOperator.Equal, "Acme/*")]
    [InlineData("name:\"a|b\"", QueryOperator.Equal, "a|b")]
    [InlineData("-name:\"a|b\"", QueryOperator.NotEqual, "a|b")]
    [InlineData("-name:\"a*\"", QueryOperator.NotIn, "a*")]
    public void AQuotedFieldValueIsALiteralComparison(string query, QueryOperator op, string value)
    {
        var condition = Parser.Parse(query).Filter.Should().BeOfType<QueryComparison>().Subject.Condition;
        condition.Operator.Should().Be(op, "no Like, no In-split; a negated literal star is a one-value NotIn so no executor globs it");
        string.Join(",", condition.Values).Should().Be(value);
    }

    /// <summary>#6752 — a ONE-value list whose value is quoted is the same literal as the bare quoted
    /// value: it anchors at exactly that namespace, never a LIKE pattern.</summary>
    [Fact]
    public void AOneValueQuotedNamespaceListIsThatNamespace()
    {
        var parsed = Parser.Parse("namespace:(\"acme/report*\")");
        parsed.Path.Should().Be("acme/report*", "the quoted value is the anchor, its `*` is literal");
        parsed.Filter.Should().BeNull("a quoted value may not come out as a LIKE pattern");
    }

    /// <summary>#6752 — a ONE-value negated list stays an EXCLUSION, quoted (literal) or not (the
    /// glob complement, as `-namespace:x*`); it was a positive Like / a positive anchor.</summary>
    [Theory]
    [InlineData("-namespace:(\"acme/report*\") partitions:all", "acme/report*")]
    [InlineData("-namespace:(acme/report*) partitions:all", "acme/report-2026")]
    [InlineData("-namespace:(acme/Docs) partitions:all", "acme/Docs")]
    public void AOneValueNegatedNamespaceListExcludes(string query, string excludedNamespace)
    {
        var parsed = Parser.Parse(query);
        parsed.Path.Should().BeNullOrEmpty("an exclusion never anchors the query");
        var condition = parsed.Filter.Should().BeOfType<QueryComparison>().Subject.Condition;
        condition.Selector.Should().Be("namespace");
        condition.Operator.Should().NotBe(QueryOperator.Like, "the negation must survive");
        Evaluator.Matches(new MeshNode("x", excludedNamespace) { Name = "x" }, parsed).Should().BeFalse($"`{excludedNamespace}` is excluded");
        Evaluator.Matches(new MeshNode("y", "other/ns") { Name = "y" }, parsed).Should().BeTrue("an unrelated namespace is kept");
    }

    /// <summary>#6752 negative control — the quoted negated list is LITERAL: a namespace the `*`
    /// would glob is kept.</summary>
    [Fact]
    public void AOneValueNegatedQuotedListDoesNotGlob()
    {
        var parsed = Parser.Parse("-namespace:(\"acme/report*\") partitions:all");
        Evaluator.Matches(new MeshNode("z", "acme/report-2026") { Name = "z" }, parsed).Should().BeTrue("the quoted `*` is literal");
    }

    /// <summary>#6752 negative control — an UNQUOTED one-value list keeps its glob.</summary>
    [Fact]
    public void AnUnquotedOneValueNamespaceListStillGlobs()
    {
        var condition = Parser.Parse("namespace:(acme/report*) partitions:all").Filter.Should().BeOfType<QueryComparison>().Subject.Condition;
        condition.Operator.Should().Be(QueryOperator.Like);
    }
}
