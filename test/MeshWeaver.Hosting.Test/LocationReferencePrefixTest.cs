using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A location written INTO the query text resolves the way the tools' <c>basePath</c> argument
/// does (#6828). <c>@X</c> is how every tool argument, markdown link and autocomplete writes a
/// path, and the tools strip the <c>@</c> there — but the parser kept it, so
/// <c>namespace:@acme name:"Jane Doe"</c> anchored at a partition literally called <c>@acme</c> and
/// answered <c>count: 0</c> for nodes that exist. And <c>basePath:</c>, the word the tools
/// advertise, was not a qualifier at all: it became a FILTER on a property no node has.
///
/// <para>These assertions are on <see cref="ParsedQuery"/>, which BOTH executors consume — the
/// in-memory evaluator and the Postgres planner/SQL generator (MeshWeaver.Plugins) read the same
/// <see cref="ParsedQuery.Path"/>, <see cref="ParsedQuery.Scope"/> and
/// <see cref="ParsedQuery.NamedPartitions"/> — so the resolution cannot differ between them.</para>
/// </summary>
public class LocationReferencePrefixTest
{
    private static ParsedQuery Parse(string query) => new QueryParser().Parse(query);

    [Theory]
    [InlineData("namespace:@acme name:\"Jane Doe\"", "acme", QueryScope.Children)]
    [InlineData("namespace:@/acme/Deals scope:descendants", "acme/Deals", QueryScope.Descendants)]
    [InlineData("path:@acme/Deals/Jane", "acme/Deals/Jane", QueryScope.Exact)]
    [InlineData("path:@/acme scope:subtree", "acme", QueryScope.Subtree)]
    [InlineData("namespace:\"@acme\"", "acme", QueryScope.Children)]
    public void TheReferencePrefixComesOffAnInQueryAnchor(string query, string path, QueryScope scope)
    {
        var parsed = Parse(query);

        parsed.Path.Should().Be(path, $"'{query}' anchors at the path the reference names, not at a partition called '@…'");
        parsed.Scope.Should().Be(scope);
        parsed.NamedPartitions().Should().Equal(path.Split('/')[0]);
    }

    [Fact]
    public void TheReferencePrefixComesOffEveryAlternative()
    {
        var parsed = Parse("path:@acme/A|@/beta/B");

        parsed.Paths.Should().Equal("acme/A", "beta/B");
        parsed.NamedPartitions().Should().Equal("acme", "beta");
    }

    [Fact]
    public void AnExclusionResolvesItsReferenceToo()
    {
        var parsed = Parse("namespace:acme scope:descendants -namespace:@acme/Archive");

        parsed.Path.Should().Be("acme");
        parsed.Filter.Should().BeOfType<QueryComparison>()
            .Which.Condition.Value.Should().Be("acme/Archive", "the exclusion names the namespace the reference names");
    }

    [Theory]
    [InlineData("name:\"Jane Doe\" basePath:@acme", "acme")]
    [InlineData("basePath:acme/Deals nodeType:Crm/Contact", "acme/Deals")]
    [InlineData("basePath:@/acme name:\"Jane Doe\"", "acme")]
    [InlineData("basePath:(@acme) name:\"Jane Doe\"", "acme")]
    public void BasePathIsTheDescendantsOfItsPath_NotAFilter(string query, string path)
    {
        var parsed = Parse(query);

        parsed.Path.Should().Be(path);
        parsed.Scope.Should().Be(QueryScope.Descendants, "basePath means everything UNDER the path, as the tool argument does");
        parsed.IsSufficientlySpecified().Should().BeTrue("basePath anchors the query");
        ContainsSelector(parsed.Filter, QueryParser.BasePathQualifier).Should().BeFalse(
            "basePath is a location, never a filter on a property no node has");
    }

    /// <summary>
    /// A WILDCARD <c>basePath:</c> sets no concrete anchor, and still means the descendants: it
    /// parses exactly as its documented equivalent <c>namespace:… scope:descendants</c> — the
    /// pattern widened to self-or-below — never an exact-scope LIKE that misses deeper matches
    /// (review of #6836).
    /// </summary>
    [Fact]
    public void AWildcardBasePathIsTheDescendantsOfThePattern()
    {
        var parsed = Parse("basePath:@acme/*/Contacts nodeType:Crm/Contact");
        var equivalent = Parse("namespace:acme/*/Contacts scope:descendants nodeType:Crm/Contact");

        parsed.Scope.Should().Be(QueryScope.Descendants);
        parsed.Scope.Should().Be(equivalent.Scope);
        parsed.Path.Should().Be(equivalent.Path);
        NamespacePatterns(parsed.Filter).Should().Equal(NamespacePatterns(equivalent.Filter));
        NamespacePatterns(parsed.Filter).Should().Contain($"{QueryOperator.Like}:acme/*/Contacts/*","the pattern is widened to everything below it");
        ContainsSelector(parsed.Filter, QueryParser.BasePathQualifier).Should().BeFalse();
    }

    [Fact]
    public void BasePathKeepsAnExplicitScope()
    {
        var parsed = Parse("basePath:@acme scope:children");

        parsed.Path.Should().Be("acme");
        parsed.Scope.Should().Be(QueryScope.Children);
    }

    /// <summary>
    /// The negative control on the strip: a value that does not LEAD with <c>@</c> is untouched —
    /// a partition named after an e-mail address keeps its <c>@</c>.
    /// </summary>
    [Fact]
    public void AnAtInsideAPathIsNotAPrefix()
    {
        var parsed = Parse("namespace:jane@example.com scope:descendants");

        parsed.Path.Should().Be("jane@example.com");
    }

    private static string[] NamespacePatterns(QueryNode? node) => node switch
    {
        QueryComparison c when c.Condition.Selector.Equals("namespace", StringComparison.OrdinalIgnoreCase)
            => c.Condition.Values.Select(v => $"{c.Condition.Operator}:{v}").ToArray(),
        QueryAnd a => a.Children.SelectMany(NamespacePatterns).ToArray(),
        QueryOr o => o.Children.SelectMany(NamespacePatterns).ToArray(),
        _ => [],
    };

    private static bool ContainsSelector(QueryNode? node, string selector) => node switch
    {
        QueryComparison c => c.Condition.Selector.Equals(selector, StringComparison.OrdinalIgnoreCase),
        QueryAnd a => a.Children.Any(n => ContainsSelector(n, selector)),
        QueryOr o => o.Children.Any(n => ContainsSelector(n, selector)),
        _ => false,
    };
}
