using System;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The user home's catalog is ONE tab-less, FLAT, FIRST-LEVEL list — a single reactive
/// <see cref="MeshSearchControl"/> (NOT a tab control, NOT grouped-by-type) showing only the TOP-LEVEL
/// entries the viewer can see: the partition roots (spaces, courses, plugins) plus the user's own
/// top-level home items — NOT the whole tree, and never the user's own root node. It DEFAULTS to
/// last-accessed order and exposes a view-options "Sort by" control — Last accessed (default),
/// Last modified, Alphabetical. The one gap
/// this can't reach — a module in ANOTHER partition the caller was invited into (#385) — is resolved
/// from the caller's own <c>AccessAssignment</c> grants (<see cref="UserActivityLayoutAreas.SharedTargetPaths"/>)
/// and appended as an additive "Shared with me" band, present only when such grants exist.
/// </summary>
public class HomeCatalogTest
{
    private const string NodePath = "rbuergi";

    [Fact]
    public void Catalog_IsASingleTablessFlatList()
    {
        var catalog = UserActivityLayoutAreas.BuildCatalog(NodePath);

        // NO tabs — a single MeshSearch, never a TabsControl. FLAT, not grouped-by-type.
        var search = catalog.Should().BeOfType<MeshSearchControl>().Subject;
        catalog.Should().NotBeOfType<TabsControl>();
        search.RenderMode.Should().Be(MeshSearchRenderMode.Flat);
        search.ShowSearchBox.Should().Be(true);
        // View-options on → the user controls the order.
        search.ShowViewOptions.Should().Be(true);
        // Generic create (type picker), never a type-specific target.
        search.CreateHref.Should().Be("/create");
    }

    [Fact]
    public void Catalog_IsFirstLevelOnly_NotTheWholeTree()
    {
        var search = UserActivityLayoutAreas.BuildCatalog(NodePath).Should().BeOfType<MeshSearchControl>().Subject;
        var query = search.HiddenQuery!.ToString()!;

        // A UNION of two first-level sub-queries (one per line): partition roots + the user's home
        // direct children — NEITHER spans a subtree, so no deep descendants leak in.
        query.Should().Contain("namespace: is:main", "partition roots = the empty-namespace top level");
        query.Should().Contain($"namespace:{NodePath} is:main", "the user's own top-level home items");
        query.Should().NotContain("scope:subtree");
        query.Should().NotContain("scope:descendants");
    }

    [Fact]
    public void Catalog_DefaultsToLastAccessedOrder()
    {
        var search = UserActivityLayoutAreas.BuildCatalog(NodePath).Should().BeOfType<MeshSearchControl>().Subject;

        // Default order = last accessed (source:accessed projects the UserActivity access time into
        // the sort slot), most-recently-opened first. The JOIN targets the CALLER's own access log
        // (their partition's user_activities) across every branch schema, so it works
        // cross-partition — and every scope clause still applies (the roots leg pushes
        // `namespace = ''`), so it stays a first-level list.
        search.HiddenQuery!.ToString().Should().Contain("source:accessed");
        search.HiddenQuery!.ToString().Should().Contain("sort:LastModified-desc");
    }

    [Fact]
    public void Catalog_OffersLastAccessedLastModifiedAndAlphabeticalSorts()
    {
        var search = UserActivityLayoutAreas.BuildCatalog(NodePath).Should().BeOfType<MeshSearchControl>().Subject;

        var options = search.SortOptions!;
        options.Select(o => o.Label).Should().Equal("Last accessed", "Last modified", "Alphabetical");
        // The first option is the default and MUST equal the control's HiddenQuery.
        options[0].Query.Should().Be(search.HiddenQuery!.ToString());
        // Last accessed = access-ordered working set; the other two are pure order-bys.
        options[0].Query.Should().Contain("source:accessed");
        options[1].Query.Should().Contain("sort:LastModified-desc");
        options[1].Query.Should().NotContain("source:accessed");
        options[2].Query.Should().Contain("sort:Name-asc");
        options[2].Query.Should().NotContain("source:accessed");
        // Every option stays first-level (union of roots + home children, no subtree).
        foreach (var o in options)
        {
            o.Query.Should().Contain("namespace: is:main");
            o.Query.Should().Contain($"namespace:{NodePath} is:main");
            o.Query.Should().NotContain("scope:subtree");
        }
    }

    [Fact]
    public void Catalog_ExcludesTheUsersOwnRootNode()
    {
        // The viewer's own home root (path == owner, namespace == "") matches the partition-roots
        // leg (`namespace:` = empty-namespace top level), so the leg must exclude User nodes — a
        // home page never lists the user itself.
        var search = UserActivityLayoutAreas.BuildCatalog(NodePath).Should().BeOfType<MeshSearchControl>().Subject;
        search.HiddenQuery!.ToString().Should().Contain("-nodeType:User");

        var subtree = UserActivityLayoutAreas
            .BuildCatalog(NodePath, new HomeConfig { Scope = HomeCatalogScope.Subtree })
            .Should().BeOfType<MeshSearchControl>().Subject;
        subtree.HiddenQuery!.ToString().Should().Contain("-nodeType:User");
    }

    [Fact]
    public void Catalog_WithoutSharedTargets_IsJustTheFlatList()
    {
        UserActivityLayoutAreas.BuildCatalog(NodePath, sharedTargets: [])
            .Should().BeOfType<MeshSearchControl>("no cross-partition grants → literally one flat list");
    }

    [Fact]
    public void Catalog_WithSharedTargets_AppendsASharedWithMeBand()
    {
        // #385 — the caller was invited into modules in OTHER partitions; the first-level query can't
        // reach them, so they're appended as an additive "Shared with me" band below the flat list.
        var catalog = UserActivityLayoutAreas.BuildCatalog(NodePath, sharedTargets: ["OrgA/Module", "OrgB/Deck"]);

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

    // ── Data-driven home config (Admin/HomeConfig) ──────────────────────────────────────────────────

    [Fact]
    public void HomeConfig_ShippedDefaults_AreFirstLevelFlatLastAccessed()
    {
        HomeConfigNodeType.Defaults.Scope.Should().Be(HomeCatalogScope.FirstLevel);
        HomeConfigNodeType.Defaults.Render.Should().Be(HomeCatalogRender.Flat);
        HomeConfigNodeType.Defaults.DefaultSort.Should().Be(HomeCatalogSort.LastAccessed);
    }

    [Fact]
    public void Catalog_NullConfig_EqualsTheShippedDefaults()
    {
        // No config == the shipped defaults == exactly what BuildCatalog(ownerId) produces.
        var byDefault = UserActivityLayoutAreas.BuildCatalog(NodePath).Should().BeOfType<MeshSearchControl>().Subject;
        var byNull = UserActivityLayoutAreas.BuildCatalog(NodePath, config: null).Should().BeOfType<MeshSearchControl>().Subject;

        byNull.HiddenQuery!.ToString().Should().Be(byDefault.HiddenQuery!.ToString());
        byNull.RenderMode.Should().Be(MeshSearchRenderMode.Flat);
    }

    [Fact]
    public void Catalog_ConfigSubtree_QueriesTheWholeTree_NotJustFirstLevel()
    {
        var cfg = new HomeConfig { Scope = HomeCatalogScope.Subtree };
        var search = UserActivityLayoutAreas.BuildCatalog(NodePath, cfg).Should().BeOfType<MeshSearchControl>().Subject;

        var query = search.HiddenQuery!.ToString()!;
        query.Should().Contain("is:main context:search");
        query.Should().NotContain("namespace:");   // no first-level roots/home-children union
        query.Should().NotContain("\n");           // a single query, not a union
    }

    [Fact]
    public void Catalog_ConfigGrouped_RendersPerTypeSections()
    {
        var cfg = new HomeConfig { Render = HomeCatalogRender.Grouped };
        var search = UserActivityLayoutAreas.BuildCatalog(NodePath, cfg).Should().BeOfType<MeshSearchControl>().Subject;

        search.RenderMode.Should().Be(MeshSearchRenderMode.Grouped);
        search.Sections!.ShowCounts.Should().Be(true);
        search.Sections!.Collapsible.Should().Be(true);
    }

    [Fact]
    public void Catalog_ConfigDefaultSort_MakesTheChosenSortTheDefault()
    {
        var cfg = new HomeConfig { DefaultSort = HomeCatalogSort.Alphabetical };
        var search = UserActivityLayoutAreas.BuildCatalog(NodePath, cfg).Should().BeOfType<MeshSearchControl>().Subject;

        // The chosen default is FIRST and equals HiddenQuery; all three sorts are still offered.
        search.SortOptions![0].Label.Should().Be("Alphabetical");
        search.SortOptions![0].Query.Should().Be(search.HiddenQuery!.ToString());
        search.HiddenQuery!.ToString().Should().Contain("sort:Name-asc");
        search.SortOptions!.Select(o => o.Label).OrderBy(l => l, StringComparer.Ordinal).Should()
            .Equal("Alphabetical", "Last accessed", "Last modified");
    }

    [Fact]
    public void HomeConfig_Effective_FallsBackToDefaults_WhenNodeAbsent()
    {
        HomeConfigNodeType.Effective(null, new JsonSerializerOptions()).Should().Be(HomeConfigNodeType.Defaults);
    }

    [Fact]
    public void HomeConfig_Effective_ReadsTypedContent()
    {
        var options = new JsonSerializerOptions();
        var node = MeshNode.FromPath(HomeConfigNodeType.ConfigPath) with
        {
            NodeType = HomeConfigNodeType.NodeType,
            Content = new HomeConfig
            {
                Scope = HomeCatalogScope.Subtree,
                Render = HomeCatalogRender.Grouped,
                DefaultSort = HomeCatalogSort.LastModified,
            },
        };

        var effective = HomeConfigNodeType.Effective(node, options);
        effective.Scope.Should().Be(HomeCatalogScope.Subtree);
        effective.Render.Should().Be(HomeCatalogRender.Grouped);
        effective.DefaultSort.Should().Be(HomeCatalogSort.LastModified);
    }

    [Fact]
    public void HomeConfig_Effective_ReadsJsonElementContent()
    {
        var options = new JsonSerializerOptions();
        var je = JsonSerializer.SerializeToElement(new HomeConfig { Render = HomeCatalogRender.Grouped });
        var node = MeshNode.FromPath(HomeConfigNodeType.ConfigPath) with
        {
            NodeType = HomeConfigNodeType.NodeType,
            Content = je,
        };

        HomeConfigNodeType.Effective(node, options).Render.Should().Be(HomeCatalogRender.Grouped);
    }
}
