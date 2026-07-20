using System;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The user home's catalog is ONE unified, grouped "everything" view — a single reactive
/// <see cref="MeshSearchControl"/> over <c>is:main context:search</c> that spans every partition the
/// reader can see (their own items, spaces, Store/Plugin courses+plugins), grouped by type. It
/// replaces the former Spaces / My Items / Last Read / Last Edited tab row PLUS the data-driven
/// extension tabs (a tab node's content already surfaces in the unified list). The one gap a broad
/// query can't reach — a module in ANOTHER partition the caller was invited into (#385) — is resolved
/// from the caller's own <c>AccessAssignment</c> grants (<see cref="UserActivityLayoutAreas.SharedTargetPaths"/>)
/// and appended as an additive "Shared with me" band, present only when such grants exist.
/// </summary>
public class HomeCatalogTest
{
    private const string NodePath = "rbuergi";

    [Fact]
    public void Catalog_IsASingleUnifiedGroupedSearch()
    {
        var catalog = UserActivityLayoutAreas.BuildCatalog();

        var search = catalog.Should().BeOfType<MeshSearchControl>().Subject;
        // The unified query — spans every readable partition (NO namespace restriction), grouped by type.
        search.HiddenQuery!.ToString().Should().Contain("is:main");
        search.HiddenQuery!.ToString().Should().Contain("context:search");
        search.HiddenQuery!.ToString().Should().Contain("sort:LastModified-desc");
        search.HiddenQuery!.ToString().Should().NotContain("namespace:");
        search.RenderMode.Should().Be(MeshSearchRenderMode.Grouped);
        search.Sections!.ShowCounts.Should().Be(true);
        search.Sections!.Collapsible.Should().Be(true);
        search.ShowSearchBox.Should().Be(true);
        search.ShowViewOptions.Should().Be(true);
        // Generic create (type picker), never a type-specific target.
        search.CreateHref.Should().Be("/create");
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
