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

    [Fact]
    public void ADeclaredFanOutIsSpecified_AndNamesNothing()
    {
        var parsed = Parse($"nodeType:NodeType content.compilationStatus:Error {ParsedQuery.CrossPartitionQualifier}");

        parsed.IsSufficientlySpecified().Should().BeTrue("partitions:all is the explicit request to span");
        parsed.NamedPartitions().Should().BeEmpty("only the backend that ran it can state what a fan-out covered");
    }

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
