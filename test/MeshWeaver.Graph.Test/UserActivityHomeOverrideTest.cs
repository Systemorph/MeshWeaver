using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for the configurable owner home — ONE editable markdown page (the user node's
/// <see cref="User.Body"/>, 1:1 with <c>Space.Body</c>): by default it serves the welcome template
/// (which embeds the home regions with <c>@@</c>), and when <c>Body</c> is set the page respects that
/// override verbatim. Also pins the unified, grouped "everything" catalog shape.
/// </summary>
public class UserActivityHomeOverrideTest
{
    private const string NodePath = "rbuergi";
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode UserNode(User content) =>
        MeshNode.FromPath(NodePath) with { Name = "Roland", NodeType = "User", Content = content };

    private static string Markdown(UiControl control) =>
        ((MarkdownControl)control).Markdown?.ToString() ?? "";

    [Fact]
    public void OwnerHome_WithoutBody_ServesTheWelcomeTemplate()
    {
        var home = UserActivityLayoutAreas.BuildOwnerHome(NodePath, "Jane Roe", UserNode(new User()), Options);

        home.Should().BeOfType<MarkdownControl>();
        Markdown(home).Should().Be(UserActivityLayoutAreas.UserWelcomeMarkdown("Jane Roe"));
        // NodePath must be set so the welcome's relative @@("area/…") embeds resolve to this user hub.
        ((MarkdownControl)home).NodePath.Should().Be(NodePath);
    }

    [Fact]
    public void OwnerHome_WelcomeTemplate_IsTheMessageBoxThenTheLauncher_AndNothingElse()
    {
        // Policy home-is-composer-apps-spaces: a welcome heading, the message box, apps, then spaces.
        // The Catalog region is the Apps grid followed by the Spaces grid (BuildHome); the template
        // carries only the heading, the composer and that region — no cards, no footer note.
        var md = UserActivityLayoutAreas.UserWelcomeMarkdown("Jane Roe");

        var blocks = md.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        blocks.Should().Equal("# Welcome back, Jane", "@@(\"area/Composer\")", "@@(\"area/Catalog\")");
    }

    [Fact]
    public void OwnerHome_WithBody_RespectsTheOverrideVerbatim()
    {
        const string custom = "# My page\n\nJust my own words.\n\n@@(\"area/Search\")";
        var home = UserActivityLayoutAreas.BuildOwnerHome(NodePath, "Jane Roe", UserNode(new User { Body = custom }), Options);

        home.Should().BeOfType<MarkdownControl>();
        Markdown(home).Should().Be(custom);
        Markdown(home).Should().NotContain("Welcome back");
        ((MarkdownControl)home).NodePath.Should().Be(NodePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void OwnerHome_WithBlankBody_FallsBackToTheDefault(string blank)
    {
        var home = UserActivityLayoutAreas.BuildOwnerHome(NodePath, "Jane Roe", UserNode(new User { Body = blank }), Options);

        Markdown(home).Should().Be(UserActivityLayoutAreas.UserWelcomeMarkdown("Jane Roe"),
            "blank/whitespace is not an override — the default template must show");
    }

    [Fact]
    public void OwnerHome_NullContent_StillRendersTheDefault()
    {
        var node = MeshNode.FromPath(NodePath) with { Name = "Roland", NodeType = "User" };
        var home = UserActivityLayoutAreas.BuildOwnerHome(NodePath, "Jane Roe", node, Options);

        Markdown(home).Should().Be(UserActivityLayoutAreas.UserWelcomeMarkdown("Jane Roe"));
    }

    [Fact]
    public void Pinned_IsNull_WhenNothingPinned()
    {
        UserActivityLayoutAreas.BuildPinnedItems(null).Should().BeNull();
        UserActivityLayoutAreas.BuildPinnedItems(new User()).Should().BeNull();
        UserActivityLayoutAreas.BuildPinnedItems(new User { PinnedPaths = [] }).Should().BeNull();
    }

    [Fact]
    public void Pinned_RendersASearchBand_WhenItemsArePinned()
    {
        var pinned = UserActivityLayoutAreas.BuildPinnedItems(new User { PinnedPaths = ["acme/a", "acme/b"] });

        pinned.Should().BeOfType<MeshSearchControl>();
    }

    [Fact]
    public void Catalog_IsATablessFlatFirstLevelList()
    {
        var catalog = UserActivityLayoutAreas.BuildCatalog(NodePath);

        // ONE tab-less, FLAT, FIRST-LEVEL list — no tab row, no grouping, defaults to location order.
        var search = catalog.Should().BeOfType<MeshSearchControl>().Subject;
        catalog.Should().NotBeOfType<TabsControl>();
        search.HiddenQuery!.ToString().Should().Contain("sort:path-asc");
        search.HiddenQuery!.ToString().Should().NotContain("scope:subtree");
        search.RenderMode.Should().Be(MeshSearchRenderMode.Flat);
    }
}
