// <meshweaver>
// Id: Testing/Graph/SearchAreaGroupByTest
// DisplayName: Testing/Graph/SearchAreaGroupByTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using MeshWeaver.Graph;
using MeshWeaver.Layout;

/// <summary>
/// Pins the URL-driven mapping behind the node <c>Search</c> area (the catalog that replaced the
/// dedicated <c>Children</c> area): <c>?groupBy=</c> → <see cref="MeshSearchRenderMode"/> (+ the
/// node property for the Grouped modes). Documented in the "Mesh Search &amp; Catalogs" article.
/// </summary>
public class SearchAreaGroupByTest
{
    [MeshTheory]
    [MeshInlineData("namespace", MeshSearchRenderMode.NamespaceTree, null)]
    [MeshInlineData("ns", MeshSearchRenderMode.NamespaceTree, null)]
    [MeshInlineData("tree", MeshSearchRenderMode.NamespaceTree, null)]
    [MeshInlineData("type", MeshSearchRenderMode.Grouped, "NodeType")]
    [MeshInlineData("nodeType", MeshSearchRenderMode.Grouped, "NodeType")]
    [MeshInlineData("category", MeshSearchRenderMode.Grouped, "Category")]
    [MeshInlineData("cat", MeshSearchRenderMode.Grouped, "Category")]
    [MeshInlineData("flat", MeshSearchRenderMode.Flat, null)]
    [MeshInlineData("none", MeshSearchRenderMode.Flat, null)]
    [MeshInlineData("grid", MeshSearchRenderMode.Flat, null)]
    [MeshInlineData("hierarchy", MeshSearchRenderMode.Hierarchical, null)]
    [MeshInlineData("hierarchical", MeshSearchRenderMode.Hierarchical, null)]
    [MeshInlineData("TYPE", MeshSearchRenderMode.Grouped, "NodeType")] // case-insensitive
    public void ResolveCatalogView_MapsGroupBy(string groupBy, MeshSearchRenderMode expectedMode, string? expectedProperty)
    {
        var (mode, property) = MeshNodeLayoutAreas.ResolveCatalogView(groupBy, MeshSearchRenderMode.Flat);

        mode.Should().Be(expectedMode);
        property.Should().Be(expectedProperty);
    }

    [MeshTheory]
    [MeshInlineData(null)]
    [MeshInlineData("")]
    [MeshInlineData("bogus")]
    public void ResolveCatalogView_UnknownOrMissing_UsesFallback(string? groupBy)
    {
        // The fallback differs by surface (namespace tree for a content catalog, hierarchical for
        // a NodeType's instances); ResolveCatalogView just returns whatever the caller passes.
        var (mode, property) = MeshNodeLayoutAreas.ResolveCatalogView(groupBy, MeshSearchRenderMode.NamespaceTree);

        mode.Should().Be(MeshSearchRenderMode.NamespaceTree);
        property.Should().BeNull();
    }

    [MeshTheory]
    [MeshInlineData("true", true)]
    [MeshInlineData("True", true)]
    [MeshInlineData("1", true)]
    [MeshInlineData("yes", true)]
    [MeshInlineData("on", true)]
    [MeshInlineData("false", false)]
    [MeshInlineData("0", false)]
    [MeshInlineData("no", false)]
    [MeshInlineData("off", false)]
    [MeshInlineData("", false)]
    [MeshInlineData(null, false)]
    public void ParseTruthy_RecognisesCommonTruthyValues(string? value, bool expected)
    {
        // Drives every boolean catalog param (?searchBar, ?subtree, ?counts, ?emptyMessage, …).
        MeshNodeLayoutAreas.ParseTruthy(value).Should().Be(expected);
    }
}
