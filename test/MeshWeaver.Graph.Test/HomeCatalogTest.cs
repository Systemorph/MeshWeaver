using System;
using System.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The user home's catalog is ONE unified, tab-less, grouped "everything" list — a single reactive
/// <see cref="MeshSearchControl"/> (NOT a tab control) that spans every partition the reader can see
/// (their own items, spaces, Store/Plugin courses+plugins), grouped by type. It DEFAULTS to
/// last-accessed order (the user's recently-opened working set) and exposes a view-options "Sort by"
/// control — Last accessed (default), Last modified, Alphabetical — so the user controls the order. It
/// replaces the former Spaces / My Items / Last Read / Last Edited tab row PLUS the data-driven
/// extension tabs. The one gap a broad query can't reach — a module in ANOTHER partition the caller was
/// invited into (#385) — is resolved from the caller's own <c>AccessAssignment</c> grants
/// (<see cref="UserActivityLayoutAreas.SharedTargetPaths"/>) and appended as an additive "Shared with
/// me" band, present only when such grants exist.
/// </summary>
public class HomeCatalogTest
{
    private const string NodePath = "rbuergi";

    [Fact]
    public void Catalog_IsASingleTablessGroupedSearch()
    {
        var catalog = UserActivityLayoutAreas.BuildCatalog();

        // NO tabs — a single MeshSearch, never a TabsControl.
        var search = catalog.Should().BeOfType<MeshSearchControl>().Subject;
        catalog.Should().NotBeOfType<TabsControl>();
        // Spans every readable partition (NO namespace restriction), grouped by type.
        search.HiddenQuery!.ToString().Should().Contain("is:main");
        search.HiddenQuery!.ToString().Should().Contain("context:search");
        search.HiddenQuery!.ToString().Should().NotContain("namespace:");
        search.RenderMode.Should().Be(MeshSearchRenderMode.Grouped);
        search.Sections!.ShowCounts.Should().Be(true);
        search.Sections!.Collapsible.Should().Be(true);
        search.ShowSearchBox.Should().Be(true);
        // View-options on → the user controls the order.
        search.ShowViewOptions.Should().Be(true);
        // Generic create (type picker), never a type-specific target.
        search.CreateHref.Should().Be("/create");
    }

    [Fact]
    public void Catalog_DefaultsToLastAccessedOrder()
    {
        var search = UserActivityLayoutAreas.BuildCatalog().Should().BeOfType<MeshSearchControl>().Subject;

        // Default order = last accessed (source:accessed projects the UserActivity access time into the
        // sort slot), most-recently-opened first — supersedes the old "Last Read" tab.
        search.HiddenQuery!.ToString().Should().Contain("source:accessed");
        search.HiddenQuery!.ToString().Should().Contain("sort:LastModified-desc");
    }

    [Fact]
    public void Catalog_OffersLastAccessedLastModifiedAndAlphabeticalSorts()
    {
        var search = UserActivityLayoutAreas.BuildCatalog().Should().BeOfType<MeshSearchControl>().Subject;

        var options = search.SortOptions!;
        options.Select(o => o.Label).Should().Equal("Last accessed", "Last modified", "Alphabetical");
        // The first option is the default and MUST equal the control's HiddenQuery.
        options[0].Query.Should().Be(search.HiddenQuery!.ToString());
        // Last accessed = the user's accessed working set; the other two span the full readable set.
        options[0].Query.Should().Contain("source:accessed");
        options[1].Query.Should().Contain("sort:LastModified-desc");
        options[1].Query.Should().NotContain("source:accessed");
        options[2].Query.Should().Contain("sort:Name-asc");
        options[2].Query.Should().NotContain("source:accessed");
    }

    [Fact]
    public void Catalog_WithoutSharedTargets_IsJustTheUnifiedSearch()
    {
        UserActivityLayoutAreas.BuildCatalog(sharedTargets: [])
            .Should().BeOfType<MeshSearchControl>("no cross-partition grants → literally one search");
    }

    [Fact]
    public void Catalog_WithSharedTargets_AppendsASharedWithMeBand()
    {
        // #385 — the caller was invited into modules in OTHER partitions; a broad is:main query can't
        // reach them, so they're appended as an additive "Shared with me" band below the unified view.
        var catalog = UserActivityLayoutAreas.BuildCatalog(sharedTargets: ["OrgA/Module", "OrgB/Deck"]);

        // A stack of the unified search + the shared band (two areas), never a bare search.
        var stack = catalog.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().HaveCount(2);
    }

    [Fact]
    public void SharedTargetPaths_KeepsCrossPartitionTargets_ExcludesOwnPartitionAndEmpty()
    {
        MeshNode Assignment(string scope) =>
            MeshNode.FromPath($"{scope}/_Access/{NodePath}_Access") with
            {
                NodeType = "AccessAssignment",
                MainNode = scope,
            };

        var assignments = new[]
        {
            Assignment("OrgA/Module"),              // cross-partition → kept
            Assignment("OrgB/Deck"),                // cross-partition → kept
            Assignment($"{NodePath}/Private"),      // own partition → excluded
            Assignment("OrgA/Module"),              // duplicate → deduped
        };

        var targets = UserActivityLayoutAreas.SharedTargetPaths(assignments, NodePath);

        targets.Should().Equal("OrgA/Module", "OrgB/Deck");
    }

    [Fact]
    public void SharedTargetPaths_FallsBackToScopeFromPath_WhenMainNodeMissing()
    {
        // A grant persisted without MainNode still resolves via the node path's scope.
        var assignment = MeshNode.FromPath($"OrgA/Module/_Access/{NodePath}_Access") with
        {
            NodeType = "AccessAssignment",
        };

        UserActivityLayoutAreas.SharedTargetPaths([assignment], NodePath)
            .Should().Equal("OrgA/Module");
    }
}
