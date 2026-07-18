using System.Linq;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The user home's catalog tab row is DATA-driven: any readable <c>HomeTab</c> node becomes a tab
/// (label = node name, content maps the search + the "+" target). The framework knows nothing
/// about the domain a tab surfaces — e.g. the Edu plugin ships a "Courses" tab node whose "+"
/// opens the course catalog, with zero framework change.
/// </summary>
public class HomeTabExtensionTest
{
    private const string NodePath = "rbuergi";

    private static MeshNode HomeTabNode(string name, object content) =>
        MeshNode.FromPath($"Edu/{name}Tab") with
        {
            Name = name,
            NodeType = UserActivityLayoutAreas.HomeTabNodeType,
            Content = JsonSerializer.SerializeToElement(content),
        };

    [Fact]
    public void Catalog_WithoutExtensionTabs_HasTheBuiltInsOnly()
    {
        var catalog = (TabsControl)UserActivityLayoutAreas.BuildCatalog(NodePath, NodePath);

        catalog.Areas.Select(a => a.Id).Should().Equal("Spaces", "My Items", "Last Read", "Last Edited");
    }

    [Fact]
    public void Catalog_WithAHomeTabNode_InsertsTheTabAfterSpaces()
    {
        var courses = HomeTabNode("Courses", new
        {
            nodeType = "Edu/Course",
            placeholder = "Search courses…",
            createHref = "/Courses/Catalog",
        });

        var catalog = (TabsControl)UserActivityLayoutAreas.BuildCatalog(NodePath, NodePath, [courses]);

        catalog.Areas.Select(a => a.Id)
            .Should().Equal("Spaces", "Courses", "My Items", "Last Read", "Last Edited");
    }

    [Fact]
    public void Catalog_WithSharedTargets_InsertsSharedWithMeAfterSpaces()
    {
        // #385 — the caller was invited into modules in OTHER partitions; they surface as a
        // "Shared with me" tab so they're discoverable in nav (not only reachable by URL).
        var catalog = (TabsControl)UserActivityLayoutAreas.BuildCatalog(
            NodePath, NodePath, extensionTabs: null, sharedTargets: ["OrgA/Module", "OrgB/Deck"]);

        catalog.Areas.Select(a => a.Id)
            .Should().Equal("Spaces", "Shared with me", "My Items", "Last Read", "Last Edited");
    }

    [Fact]
    public void Catalog_WithoutSharedTargets_OmitsTheSharedWithMeTab()
    {
        var catalog = (TabsControl)UserActivityLayoutAreas.BuildCatalog(
            NodePath, NodePath, extensionTabs: null, sharedTargets: []);

        catalog.Areas.Select(a => a.Id).Should().NotContain("Shared with me");
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

    [Fact]
    public void MapHomeTab_MapsSearchQueryPlaceholderAndCreateHref()
    {
        var (label, search) = UserActivityLayoutAreas.MapHomeTab(HomeTabNode("Courses", new
        {
            nodeType = "Edu/Course",
            placeholder = "Search courses…",
            createHref = "/Courses/Catalog",
        }));

        label.Should().Be("Courses");
        search.HiddenQuery!.ToString().Should().Contain("nodeType:Edu/Course");
        search.HiddenQuery!.ToString().Should().Contain("is:main");
        search.Placeholder.Should().Be("Search courses…");
        search.CreateHref.Should().Be("/Courses/Catalog");
        search.ShowSearchBox.Should().Be(true);
    }

    [Fact]
    public void MapHomeTab_MinimalNode_DefaultsAreSafe()
    {
        // Only a name + empty content: a browse-everything tab with no search box and no "+".
        var (label, search) = UserActivityLayoutAreas.MapHomeTab(HomeTabNode("Feed", new { }));

        label.Should().Be("Feed");
        search.HiddenQuery!.ToString().Should().Contain("is:main");
        search.CreateHref.Should().BeNull();
        search.ShowSearchBox.Should().Be(false);
    }
}
