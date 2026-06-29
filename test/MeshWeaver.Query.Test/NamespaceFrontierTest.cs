using System;
using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Unit tests for <see cref="NamespaceFrontier"/> — the pure "next populated level" frontier behind
/// <see cref="QueryScope.NextLevel"/> (and the in-memory mirror of the Postgres anti-join). The
/// frontier is the nearest real nodes below a base path, with empty intermediate namespace segments
/// skipped.
/// </summary>
public class NamespaceFrontierTest
{
    // Order-insensitive set equality via the codebase's standard ordered .Should().Equal(...).
    private static string[] Sorted(System.Collections.Generic.IEnumerable<string> items)
        => items.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Frontier_SkipsEmptyIntermediateSegments_SurfacesDeepNode()
    {
        // a and a/b are NOT real nodes — only a/b/node exists. It must surface at the root.
        NamespaceFrontier.Frontier("", new[] { "a/b/node" }).Should().Equal("a/b/node");
    }

    [Fact]
    public void Frontier_RealAncestor_SuppressesDeeperNode()
    {
        // a is real → it is the frontier at root; a/b/node is hidden behind it.
        NamespaceFrontier.Frontier("", new[] { "a", "a/b/node" }).Should().Equal("a");
    }

    [Fact]
    public void Frontier_FromRealAncestor_RevealsNextDeepNode()
    {
        // Navigating INTO a, its next level is a/b/node (b is an empty segment).
        NamespaceFrontier.Frontier("a", new[] { "a", "a/b/node" }).Should().Equal("a/b/node");
    }

    [Fact]
    public void Frontier_Branching_ReturnsNearestPerBranch_AtDifferentDepths()
    {
        // p/x is a direct child; p/y/deep is two hops (y is empty). Both are on the frontier.
        Sorted(NamespaceFrontier.Frontier("p", new[] { "p/x", "p/y/deep" }))
            .Should().Equal("p/x", "p/y/deep");
    }

    [Fact]
    public void Frontier_ExcludesSelf()
    {
        NamespaceFrontier.Frontier("p", new[] { "p", "p/x" }).Should().Equal("p/x");
    }

    [Fact]
    public void Frontier_Siblings_BothReturned()
    {
        Sorted(NamespaceFrontier.Frontier("p", new[] { "p/a", "p/b" }))
            .Should().Equal("p/a", "p/b");
    }

    [Fact]
    public void Frontier_RealDirectChild_SuppressesItsOwnChild()
    {
        NamespaceFrontier.Frontier("p", new[] { "p/a", "p/a/b" }).Should().Equal("p/a");
    }

    [Fact]
    public void Frontier_IsCaseInsensitive_ForSuppression()
    {
        // "A" (different casing) still suppresses "a/b".
        NamespaceFrontier.Frontier("", new[] { "A", "a/b" }).Should().Equal("A");
    }

    [Fact]
    public void Frontier_SegmentBoundaryAware_DoesNotTreatPrefixSubstringAsAncestor()
    {
        // "a/bc" is NOT a descendant of "a/b" — both are distinct frontier nodes.
        Sorted(NamespaceFrontier.Frontier("a", new[] { "a/b", "a/bc" }))
            .Should().Equal("a/b", "a/bc");
    }

    [Fact]
    public void Frontier_RootBase_NearestRealNodesAcrossBranches()
    {
        var nodes = new[] { "x", "y/m/leaf", "y/n" };
        // x real → frontier; under y (empty) the nearest reals are y/m/leaf and y/n.
        Sorted(NamespaceFrontier.Frontier("", nodes)).Should().Equal("x", "y/m/leaf", "y/n");
    }

    [Fact]
    public void Frontier_NormalizesLeadingTrailingSlashes()
    {
        NamespaceFrontier.Frontier("/p/", new[] { "/p/a/", "p/a/b" }).Should().Equal("/p/a/");
    }
}
