// <meshweaver>
// Id: Testing/Graph/UserActivityHomeOverrideTest
// DisplayName: Testing/Graph/UserActivityHomeOverrideTest — migrated from xunit (convert-xunit-to-inmesh.py)
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
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

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

    [MeshFact]
    public void OwnerHome_WithoutBody_ServesTheWelcomeTemplate()
    {
        var home = UserActivityLayoutAreas.BuildOwnerHome(NodePath, "Roland", UserNode(new User()), Options);

        home.Should().BeOfType<MarkdownControl>();
        Markdown(home).Should().Contain("Welcome back, Roland");
        // NodePath must be set so the welcome's relative @@("area/…") embeds resolve to this user hub.
        ((MarkdownControl)home).NodePath.Should().Be(NodePath);
    }

    [MeshFact]
    public void OwnerHome_WelcomeTemplate_HeadingOnTop_ComposerThenRegions_TextAtBottom()
    {
        var md = UserActivityLayoutAreas.UserWelcomeMarkdown("Roland");

        md.Should().Contain("### Welcome back, Roland");
        md.Should().Contain("@@(\"area/Composer\")");
        md.Should().Contain("@@(\"area/Catalog\")");
        // Pinned is a TAB of the Catalog region now (BuildHome) — no separate bottom band, or the
        // pins would render twice on the default home. And no open-threads band either: the
        // THREADS APP (an ordinary {owner}/_App record on the Apps grid) replaced it.
        md.Should().NotContain("@@(\"area/Pinned\")");
        md.Should().NotContain("@@(\"area/Threads\")");

        var welcome = md.IndexOf("Welcome back", StringComparison.Ordinal);
        var composer = md.IndexOf("area/Composer", StringComparison.Ordinal);
        var catalog = md.IndexOf("area/Catalog", StringComparison.Ordinal);
        var configurable = md.IndexOf("configurable", StringComparison.Ordinal);

        // The welcome heading is back at the very top — above the chat composer.
        welcome.Should().BeLessThan(composer, "the welcome heading must be at the top of the home page");
        // Chat composer above the regions.
        composer.Should().BeLessThan(catalog, "the chat composer sits above the regions");
        // The configurable note sits at the BOTTOM — after the regions (the only "configurable" text).
        configurable.Should().BeGreaterThan(catalog, "the configurable text must be at the bottom of the page");
    }

    [MeshFact]
    public void OwnerHome_WelcomeTemplate_LinksToTheConfigGuide()
    {
        var md = UserActivityLayoutAreas.UserWelcomeMarkdown("Roland");

        md.Should().Contain("configurable");
        md.Should().Contain(UserActivityLayoutAreas.ConfigGuideLink);
        md.Should().Contain("/Doc/GUI/ConfigurablePages");
    }

    [MeshFact]
    public void OwnerHome_WithBody_RespectsTheOverrideVerbatim()
    {
        const string custom = "# My page\n\nJust my own words.\n\n@@(\"area/Search\")";
        var home = UserActivityLayoutAreas.BuildOwnerHome(NodePath, "Roland", UserNode(new User { Body = custom }), Options);

        home.Should().BeOfType<MarkdownControl>();
        Markdown(home).Should().Be(custom);
        Markdown(home).Should().NotContain("Welcome back");
        ((MarkdownControl)home).NodePath.Should().Be(NodePath);
    }

    [MeshTheory]
    [MeshInlineData("")]
    [MeshInlineData("   ")]
    [MeshInlineData("\n\t ")]
    public void OwnerHome_WithBlankBody_FallsBackToTheDefault(string blank)
    {
        var home = UserActivityLayoutAreas.BuildOwnerHome(NodePath, "Roland", UserNode(new User { Body = blank }), Options);

        Markdown(home).Should().Contain("Welcome back, Roland",
            "blank/whitespace is not an override — the default template must show");
    }

    [MeshFact]
    public void OwnerHome_NullContent_StillRendersTheDefault()
    {
        var node = MeshNode.FromPath(NodePath) with { Name = "Roland", NodeType = "User" };
        var home = UserActivityLayoutAreas.BuildOwnerHome(NodePath, "Roland", node, Options);

        Markdown(home).Should().Contain("Welcome back, Roland");
    }

    [MeshFact]
    public void Pinned_IsNull_WhenNothingPinned()
    {
        UserActivityLayoutAreas.BuildPinnedItems(null).Should().BeNull();
        UserActivityLayoutAreas.BuildPinnedItems(new User()).Should().BeNull();
        UserActivityLayoutAreas.BuildPinnedItems(new User { PinnedPaths = [] }).Should().BeNull();
    }

    [MeshFact]
    public void Pinned_RendersASearchBand_WhenItemsArePinned()
    {
        var pinned = UserActivityLayoutAreas.BuildPinnedItems(new User { PinnedPaths = ["acme/a", "acme/b"] });

        pinned.Should().BeOfType<MeshSearchControl>();
    }

    [MeshFact]
    public void Catalog_IsATablessFlatFirstLevelList()
    {
        var catalog = UserActivityLayoutAreas.BuildCatalog(NodePath);

        // ONE tab-less, FLAT, FIRST-LEVEL list — no tab row, no grouping, defaults to last-accessed.
        var search = catalog.Should().BeOfType<MeshSearchControl>().Subject;
        catalog.Should().NotBeOfType<TabsControl>();
        search.HiddenQuery!.ToString().Should().Contain("source:accessed");
        search.HiddenQuery!.ToString().Should().NotContain("scope:subtree");
        search.RenderMode.Should().Be(MeshSearchRenderMode.Flat);
    }
}
