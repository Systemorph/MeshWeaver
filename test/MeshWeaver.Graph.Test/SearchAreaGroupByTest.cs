#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using MeshWeaver.Graph;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the URL-driven mapping behind the node <c>Search</c> area (the catalog that replaced the
/// dedicated <c>Children</c> area): <c>?groupBy=</c> → <see cref="MeshSearchRenderMode"/> (+ the
/// node property for the Grouped modes). Documented in the "Mesh Search &amp; Catalogs" article.
/// </summary>
public class SearchAreaGroupByTest
{
    [Theory]
    [InlineData("namespace", MeshSearchRenderMode.NamespaceTree, null)]
    [InlineData("ns", MeshSearchRenderMode.NamespaceTree, null)]
    [InlineData("tree", MeshSearchRenderMode.NamespaceTree, null)]
    [InlineData("type", MeshSearchRenderMode.Grouped, "NodeType")]
    [InlineData("nodeType", MeshSearchRenderMode.Grouped, "NodeType")]
    [InlineData("category", MeshSearchRenderMode.Grouped, "Category")]
    [InlineData("cat", MeshSearchRenderMode.Grouped, "Category")]
    [InlineData("flat", MeshSearchRenderMode.Flat, null)]
    [InlineData("none", MeshSearchRenderMode.Flat, null)]
    [InlineData("grid", MeshSearchRenderMode.Flat, null)]
    [InlineData("hierarchy", MeshSearchRenderMode.Hierarchical, null)]
    [InlineData("hierarchical", MeshSearchRenderMode.Hierarchical, null)]
    [InlineData("TYPE", MeshSearchRenderMode.Grouped, "NodeType")] // case-insensitive
    public void ResolveCatalogView_MapsGroupBy(string groupBy, MeshSearchRenderMode expectedMode, string? expectedProperty)
    {
        var (mode, property) = MeshNodeLayoutAreas.ResolveCatalogView(groupBy, MeshSearchRenderMode.Flat);

        mode.Should().Be(expectedMode);
        property.Should().Be(expectedProperty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bogus")]
    public void ResolveCatalogView_UnknownOrMissing_UsesFallback(string? groupBy)
    {
        // The fallback differs by surface (namespace tree for a content catalog, hierarchical for
        // a NodeType's instances); ResolveCatalogView just returns whatever the caller passes.
        var (mode, property) = MeshNodeLayoutAreas.ResolveCatalogView(groupBy, MeshSearchRenderMode.NamespaceTree);

        mode.Should().Be(MeshSearchRenderMode.NamespaceTree);
        property.Should().BeNull();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseTruthy_RecognisesCommonTruthyValues(string? value, bool expected)
    {
        // Drives every boolean catalog param (?searchBar, ?subtree, ?counts, ?emptyMessage, …).
        MeshNodeLayoutAreas.ParseTruthy(value).Should().Be(expected);
    }
}
