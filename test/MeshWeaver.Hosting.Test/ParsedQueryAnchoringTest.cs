using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins <see cref="ParsedQuery.IsSufficientlySpecified"/> and <see cref="ParsedQuery.NamedPartitions"/>
/// — the one definition of "this query says where to look" that <c>MeshOperations.Search</c>
/// refuses on and the Postgres planner (MeshWeaver.Plugins) judges on (MeshWeaver #4274). The
/// shapes are the planner's own four ways to be specified plus the two look-alikes that are NOT:
/// a bare filter, and <c>path:*</c>, which pins to a partition literally called <c>*</c>.
/// </summary>
public class ParsedQueryAnchoringTest
{
    private static ParsedQuery Parse(string query) => new QueryParser().Parse(query);

    [Theory]
    [InlineData("nodeType:LogIncident")]
    [InlineData("nodeType:NodeType content.compilationStatus:Error")]
    [InlineData("name:*sales* sort:lastModified-desc")]
    [InlineData("path:*")]
    [InlineData("")]
    public void ABareFilterIsUnanchored(string query)
    {
        var parsed = Parse(query);

        parsed.IsSufficientlySpecified().Should().BeFalse($"'{query}' names no partition and declares no fan-out");
        parsed.NamedPartitions().Should().BeEmpty();
    }

    [Theory]
    [InlineData("namespace:Admin scope:descendants nodeType:LogIncident", "Admin")]
    [InlineData("namespace:Admin/_LogIncident nodeType:LogIncident", "Admin")]
    [InlineData("path:rbuergi nodeType:User", "rbuergi")]
    [InlineData("path:/Doc/Architecture scope:subtree", "Doc")]
    [InlineData("namespace:_Access nodeType:AccessAssignment", "_Access")]
    public void AConcreteAnchorNamesItsPartition(string query, string partition)
    {
        var parsed = Parse(query);

        parsed.IsSufficientlySpecified().Should().BeTrue($"'{query}' names a partition");
        parsed.NamedPartitions().Should().Equal(partition);
    }

    [Fact]
    public void AMultiPathAnchorNamesEveryPartitionOnce()
    {
        var parsed = Parse("path:Acme/Docs|Acme/Plans|Shared/Docs is:main");

        parsed.IsSufficientlySpecified().Should().BeTrue();
        parsed.NamedPartitions().Should().Equal(new[] { "Acme", "Shared" }, "two paths in Acme name it once");
    }

    [Fact]
    public void ANamespaceMembershipFilterIsSpecified_AndNamesItsValues()
    {
        var parsed = Parse("namespace:rbuergi/Agent|Acme/Agent|Agent nodeType:Agent");

        parsed.IsSufficientlySpecified().Should().BeTrue("the membership form is the agent registry's legitimate shape");
        parsed.NamedPartitions().Should().Equal("rbuergi/Agent", "Acme/Agent", "Agent");
    }

    [Fact]
    public void AWildcardNamespaceIsSpecified_AndIsReportedAsThePattern()
    {
        var parsed = Parse("namespace:*/_Thread nodeType:Thread");

        parsed.IsSufficientlySpecified().Should().BeTrue("the explicit wildcard is the legitimate satellite browse");
        parsed.NamedPartitions().Should().Equal(new[] { "*/_Thread" }, "a pattern names no partition; it is reported verbatim");
    }

    /// <summary>
    /// A namespace filter anchors the query only on EVERY branch: an <c>OR</c> whose other branch
    /// is a bare filter is the unanchored read wearing an anchor on one side. The subtree widening
    /// the parser performs (<c>namespace:*/X scope:subtree</c> → an OR of two patterns) is the
    /// positive control — both branches carry a pattern, so it stays specified.
    /// </summary>
    [Theory]
    [InlineData("namespace:*/_Thread OR nodeType:Foo", false)]
    [InlineData("nodeType:Foo OR namespace:*/_Thread", false)]
    [InlineData("namespace:*/_Thread OR namespace:*/_Comment", true)]
    [InlineData("namespace:*/_Thread scope:subtree nodeType:Thread", true)]
    [InlineData("(namespace:*/_Thread OR nodeType:Foo) nodeType:Thread", false)]
    public void ANamespaceFilterAnchorsOnlyWhenEveryBranchCarriesOne(string query, bool specified)
    {
        Parse(query).IsSufficientlySpecified().Should().Be(specified, $"'{query}'");
    }

    [Fact]
    public void ADeclaredFanOutIsSpecified_AndNamesNothing()
    {
        var parsed = Parse($"nodeType:NodeType content.compilationStatus:Error {ParsedQuery.CrossPartitionQualifier}");

        parsed.IsSufficientlySpecified().Should().BeTrue("partitions:all is the explicit request to span");
        parsed.NamedPartitions().Should().BeEmpty("only the backend that ran it can state what a fan-out covered");
    }

    /// <summary>
    /// 🚨 A NEGATED path is a FILTER, never an anchor (#4296). `-path:A` and `-path:A|B` say
    /// "everywhere EXCEPT A" — the one statement that cannot be served by pinning to A. The parser
    /// used to drop the operator and keep the value: the single form fell through to
    /// <c>path = value</c> and the alternation set <c>Paths = Values</c> alongside
    /// <c>Path = Values[0]</c>, so every consumer read the exclusion as a positive anchor,
    /// <c>ResolvePinnedPartition</c> pinned the query to A's schema and the per-schema route applied
    /// <c>path IN (A, B)</c> — returning rows from exactly the paths the caller excluded, or
    /// nothing, with no error. Same class as the ordered comparisons carved out in #2186.
    /// </summary>
    [Theory]
    [InlineData("-path:Acme/Secret nodeType:User")]
    [InlineData("-path:Acme/Secret|Acme/Draft nodeType:User")]
    public void ANegatedPathIsNeverAnAnchor(string query)
    {
        var parsed = Parse(query);

        parsed.Path.Should().BeNull("an exclusion names no base path to walk from");
        parsed.Paths.Should().BeNull("Paths is pushed down as `path IN (...)`, which is the opposite of the exclusion");
        parsed.NamedPartitions().Should().BeEmpty("the excluded partition is not the partition to read");
        parsed.IsSufficientlySpecified().Should().BeFalse(
            $"'{query}' says where NOT to look, which never says where TO look — it needs an anchor or {ParsedQuery.CrossPartitionQualifier}");
    }

    /// <summary>
    /// The other half: the exclusion must still be APPLIED. Dropping the token instead of filing it
    /// as a filter would trade a wrong anchor for a query that quietly returns the excluded rows.
    /// </summary>
    [Theory]
    [InlineData("-path:Acme/Secret namespace:Acme", QueryOperator.NotEqual, 1)]
    [InlineData("-path:Acme/Secret|Acme/Draft namespace:Acme", QueryOperator.NotIn, 2)]
    public void ANegatedPathSurvivesAsAFilterCondition(string query, QueryOperator expected, int values)
    {
        var parsed = Parse(query);

        var condition = Conditions(parsed.Filter)
            .Should().ContainSingle(c => c.Selector.Equals("path", StringComparison.OrdinalIgnoreCase))
            .Subject;
        condition.Operator.Should().Be(expected);
        condition.Values.Should().HaveCount(values);
        parsed.IsSufficientlySpecified().Should().BeTrue("namespace:Acme is what anchors this one");
    }

    /// <summary>A positive path is unaffected — the carve-out is for negation only.</summary>
    [Fact]
    public void APositivePathStillAnchors()
    {
        var parsed = Parse("path:Acme/Docs nodeType:User");

        parsed.Path.Should().Be("Acme/Docs");
        parsed.NamedPartitions().Should().Equal("Acme");
    }

    private static IEnumerable<QueryCondition> Conditions(QueryNode? node) => node switch
    {
        QueryComparison c => [c.Condition],
        QueryAnd a => a.Children.SelectMany(Conditions),
        QueryOr o => o.Children.SelectMany(Conditions),
        _ => []
    };

    /// <summary>
    /// The fixture's shared classifier reads the same definition — so the corpus tests that pin
    /// the planner's verdict in MeshWeaver.Plugins pin this method, not a copy of it.
    /// </summary>
    [Theory]
    [InlineData("nodeType:LogIncident", false)]
    [InlineData("namespace:Admin scope:descendants nodeType:LogIncident", true)]
    [InlineData("nodeType:LogIncident partitions:all", true)]
    public void TheFixtureClassifierReadsTheSameDefinition(string query, bool specified)
    {
        QueryRouteClassifier.IsSufficientlySpecified(Parse(query)).Should().Be(specified);
        Parse(query).IsSufficientlySpecified().Should().Be(specified);
    }
}
