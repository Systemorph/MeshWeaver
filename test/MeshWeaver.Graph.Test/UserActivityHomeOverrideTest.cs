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
/// override verbatim. Also pins the catalog 100%-width fix.
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
        var home = UserActivityLayoutAreas.BuildOwnerHome(NodePath, "Roland", UserNode(new User()), Options);

        home.Should().BeOfType<MarkdownControl>();
        Markdown(home).Should().Contain("Welcome back, Roland");
        // NodePath must be set so the welcome's relative @@("area:…") embeds resolve to this user hub.
        ((MarkdownControl)home).NodePath.Should().Be(NodePath);
    }

    [Fact]
    public void OwnerHome_WelcomeTemplate_EmbedsRegions_PinnedThenThreads()
    {
        var md = UserActivityLayoutAreas.UserWelcomeMarkdown("Roland");

        md.Should().Contain("@@(\"area:Pinned\")");
        md.Should().Contain("@@(\"area:Threads\")");
        md.Should().Contain("@@(\"area:Catalog\")");
        md.Should().Contain("@@(\"area:Composer\")");
        // Ordering requirement: pinned first, then open threads.
        md.IndexOf("area:Pinned", StringComparison.Ordinal)
            .Should().BeLessThan(md.IndexOf("area:Threads", StringComparison.Ordinal));
    }

    [Fact]
    public void OwnerHome_WelcomeTemplate_LinksToTheConfigGuide()
    {
        var md = UserActivityLayoutAreas.UserWelcomeMarkdown("Roland");

        md.Should().Contain("configurable");
        md.Should().Contain(UserActivityLayoutAreas.ConfigGuideLink);
        md.Should().Contain("/Doc/GUI/ConfigurablePages");
    }

    [Fact]
    public void OwnerHome_WithBody_RespectsTheOverrideVerbatim()
    {
        const string custom = "# My page\n\nJust my own words.\n\n@@(\"area:Search\")";
        var home = UserActivityLayoutAreas.BuildOwnerHome(NodePath, "Roland", UserNode(new User { Body = custom }), Options);

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
        var home = UserActivityLayoutAreas.BuildOwnerHome(NodePath, "Roland", UserNode(new User { Body = blank }), Options);

        Markdown(home).Should().Contain("Welcome back, Roland",
            "blank/whitespace is not an override — the default template must show");
    }

    [Fact]
    public void OwnerHome_NullContent_StillRendersTheDefault()
    {
        var node = MeshNode.FromPath(NodePath) with { Name = "Roland", NodeType = "User" };
        var home = UserActivityLayoutAreas.BuildOwnerHome(NodePath, "Roland", node, Options);

        Markdown(home).Should().Contain("Welcome back, Roland");
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
    public void Catalog_IsFullWidth()
    {
        var catalog = UserActivityLayoutAreas.BuildCatalog(NodePath, NodePath);

        catalog.Should().BeOfType<TabsControl>();
        ((TabsControl)catalog).Skin.Width.Should().Be("100%",
            "the catalog tabs must fill the home width, not shrink to content");
    }
}
