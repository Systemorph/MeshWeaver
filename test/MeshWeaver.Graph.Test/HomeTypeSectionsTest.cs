using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Apps;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The home's TYPE SECTIONS below the Apps band (<see cref="HomeTypeSections"/>, policy
/// <c>home-is-composer-apps-types</c>, Doc/Architecture/AppsHome → "Type sections"): one section per
/// node type the viewer has content of, never one for a type an installed app catalogs, never an
/// empty one, each titled by its own type — and the viewer's hide/show/order applied.
/// </summary>
public class HomeTypeSectionsTest
{
    private const string Owner = "alice";

    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    private static MeshNode Row(string path, string nodeType, int minutes) =>
        MeshNode.FromPath(path) with { NodeType = nodeType, LastModified = T0.AddMinutes(minutes) };

    private static readonly MeshNode[] Rows =
    [
        Row("Acme", "Space", 5),
        Row("Globex", "Space", 1),
        Row("alice/Notes", "Markdown", 30),
        Row("alice/Plan", "Markdown", 2),
        Row("alice/Mails", "Essentials/Mailbox", 50),
        Row("alice/Scheduling", "Scheduling/Workspace", 40),
        Row("alice/Deck", "Slides/Deck", 10),
        Row("alice/_App/Store", AppNodeType.NodeType, 60),
        Row("alice", UserNodeType.NodeType, 70),
    ];

    private static readonly CatalogedTypeSet Cataloged = CatalogedTypeSet.Of("Essentials/Mailbox", "Scheduling/Workspace");

    private static ImmutableList<string> Legs => HomeTypeSections.ContentLegs(Owner, ["Acme", "Globex"], ["OrgA/Module"]);

    // ── Discovery: excluded types never appear, empty types never appear ────────────────────────

    [Fact]
    public void A_type_an_installed_app_catalogs_gets_no_section()
    {
        var types = HomeTypeSections.Discover(Rows, Cataloged).Select(s => s.NodeType);

        types.Should().NotContain("Essentials/Mailbox", "the mail app is where mailboxes are browsed");
        types.Should().NotContain("Scheduling/Workspace", "the scheduling app catalogs its workspaces");
    }

    [Fact]
    public void The_homes_own_plumbing_gets_no_section()
    {
        var types = HomeTypeSections.Discover(Rows, CatalogedTypeSet.Empty).Select(s => s.NodeType);

        types.Should().NotContain(AppNodeType.NodeType).And.NotContain(UserNodeType.NodeType);
    }

    [Fact]
    public void Only_types_with_content_appear_newest_first_with_their_counts()
    {
        var sections = HomeTypeSections.Discover(Rows, Cataloged);

        sections.Select(s => (s.NodeType, s.Count)).Should().Equal(
            [("Markdown", 2), ("Slides/Deck", 1), ("Space", 2)],
            "a type with no rows has no section; the default order is the newest change first");
        HomeTypeSections.Discover([], Cataloged).Should().BeEmpty("no content, no section");
    }

    [Fact]
    public void A_row_two_legs_return_counts_once()
    {
        HomeTypeSections.Discover([Row("Acme", "Space", 1), Row("Acme", "Space", 1)], CatalogedTypeSet.Empty)
            .Single().Count.Should().Be(1);
    }

    // ── The viewer's arrangement ────────────────────────────────────────────────────────────────

    [Fact]
    public void Hidden_types_leave_the_home_and_wait_on_the_customize_page()
    {
        var prefs = new HomeSections().SetHidden("Markdown", true);

        var arranged = HomeTypeSections.Arrange(HomeTypeSections.Discover(Rows, Cataloged), prefs, Legs);

        arranged.Shown.Select(s => s.NodeType).Should().Equal("Slides/Deck", "Space");
        arranged.Hidden.Select(s => s.NodeType).Should().Equal("Markdown");
        HomeTypeSections.Build(arranged, Owner, null, canCustomize: true).Should().BeOfType<StackControl>();
        SectionTitles(HomeTypeSections.Build(arranged, Owner, null, canCustomize: true))
            .Should().Equal("Decks", "Spaces");
    }

    [Fact]
    public void The_viewers_order_comes_first_then_the_default_for_the_rest()
    {
        var prefs = new HomeSections { Order = ["Space", "NoLongerThere"] };

        HomeTypeSections.Arrange(HomeTypeSections.Discover(Rows, Cataloged), prefs, Legs)
            .Shown.Select(s => s.NodeType).Should().Equal("Space", "Markdown", "Slides/Deck");
    }

    [Fact]
    public void Moving_writes_the_whole_visible_order_and_stops_at_the_ends()
    {
        IReadOnlyList<string> visible = ["Markdown", "Slides/Deck", "Space"];

        new HomeSections().Move("Space", -1, visible).Order.Should().Equal("Markdown", "Space", "Slides/Deck");
        new HomeSections().Move("Markdown", -1, visible).Order.Should().BeEmpty("already at the top: nothing to write");
        new HomeSections().Move("Unknown", 1, visible).Order.Should().BeEmpty();
        new HomeSections().SetHidden("Markdown", true).SetHidden("markdown", false).Hidden.Should().BeEmpty();
    }

    [Fact]
    public void Two_moves_before_a_re_render_compose_against_the_stored_order()
    {
        // The customize page is a snapshot; a second click can land before it re-renders. Each move
        // is computed from the arrangement it patches, never from the rendered order.
        var arranged = HomeTypeSections.Arrange(HomeTypeSections.Discover(Rows, Cataloged), null, Legs);

        // Rendered order: Documents, Decks, Spaces. Up on Spaces, then Up on Decks — both from the
        // same, stale page.
        var stored = new[]
            {
                HomeTypeSections.MoveChange(arranged, "Space", -1),
                HomeTypeSections.MoveChange(arranged, "Slides/Deck", -1),
            }
            .Aggregate(new HomeSections(), (prefs, change) => change(prefs));

        HomeTypeSections.Arrange(HomeTypeSections.Discover(Rows, Cataloged), stored, Legs)
            .Shown.Select(s => s.NodeType).Should().Equal(["Markdown", "Slides/Deck", "Space"],
                "Spaces moved above Decks; Decks, by then third, moved up one — from the stored order, not the page's");
    }

    // ── The rendered sections ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Each_section_is_titled_by_its_own_type_and_carries_no_launcher_chrome_or_empty_copy()
    {
        var arranged = HomeTypeSections.Arrange(HomeTypeSections.Discover(Rows, Cataloged), null, Legs);
        var sections = Sections(HomeTypeSections.Build(arranged, Owner, null, canCustomize: true));

        sections.Select(s => s.Title!.ToString()).Should().Equal("Documents", "Decks", "Spaces");
        sections.Should().OnlyContain(s => Equals(s.ShowSearchBox, false), "no per-section search box");
        sections.Should().OnlyContain(s => Equals(s.ShowEmptyMessage, false), "no empty copy — a section exists because it has content");
        sections.Should().OnlyContain(s => s.NavigateToMainNode == null && s.ScopeTabs == null,
            "a type section is no launcher: no app filter, no view switch, no \"No apps yet\"");
        sections.Should().OnlyContain(s => s.Sortable == null);
    }

    [Fact]
    public void A_sections_query_is_its_type_over_the_anchored_legs()
    {
        var space = HomeTypeSections.SectionQuery(Legs, "Space").Split('\n');
        space.Should().HaveCount(3);
        space[0].Should().StartWith("path:Acme|Globex ").And.EndWith("nodeType:Space sort:LastModified-desc");
        space[1].Should().Contain("namespace:alice ").And.Contain("nodeType:Space");

        var docs = HomeTypeSections.SectionQuery(Legs, "Markdown").Split('\n');
        docs.Should().HaveCount(2, "the spaces leg holds only Space roots");
        docs.Should().OnlyContain(leg => leg.Contains("nodeType:Markdown"));
        docs.Concat(space).Should().OnlyContain(leg => !leg.Contains(ParsedQuery.CrossPartitionQualifier));
    }

    [Fact]
    public void Nothing_to_show_renders_nothing_not_an_empty_band()
    {
        var empty = HomeTypeSections.Arrange([], null, Legs);

        Views(HomeTypeSections.Build(empty, Owner, null, canCustomize: true)).Should().BeEmpty(
            "no section, no title, no Customize link when the viewer has nothing of a non-catalogued type");
    }

    [Fact]
    public void Only_the_owner_gets_the_customize_link()
    {
        var arranged = HomeTypeSections.Arrange(HomeTypeSections.Discover(Rows, Cataloged), null, Legs);

        Views(HomeTypeSections.Build(arranged, Owner, null, canCustomize: true)).OfType<MarkdownControl>()
            .Should().ContainSingle().Which.Markdown!.ToString().Should().Contain($"(/{Owner}/{HomeTypeSections.CustomizeArea})");
        Views(HomeTypeSections.Build(arranged, Owner, null, canCustomize: false)).OfType<MarkdownControl>()
            .Should().BeEmpty();
    }

    [Fact]
    public void The_customize_page_offers_hide_and_move_for_shown_and_show_for_hidden()
    {
        var arranged = HomeTypeSections.Arrange(HomeTypeSections.Discover(Rows, Cataloged),
            new HomeSections().SetHidden("Space", true), Legs);
        var page = HomeTypeSections.BuildCustomize(arranged, Owner, null,
            _ => System.Reactive.Linq.Observable.Return(System.Reactive.Unit.Default));

        var text = string.Join("\n", Flatten(page).OfType<ButtonControl>().Select(b => b.Data?.ToString()));
        text.Should().Contain("Hide").And.Contain("Show").And.Contain("↑").And.Contain("↓");
        Flatten(page).OfType<LabelControl>().Select(l => l.Data?.ToString())
            .Should().Contain(["Documents (2)", "Decks (1)", "Spaces (2)"]);
    }

    [Fact]
    public void An_app_root_declares_the_types_it_catalogs()
    {
        var root = AppDirectory.ReadRoot(MeshNode.FromPath("Crm") with
        {
            NodeType = "Space",
            Content = System.Text.Json.JsonDocument.Parse("""{"app":true,"catalogs":["Crm/Client"," Crm/Contact ",""]}""").RootElement,
        }, new System.Text.Json.JsonSerializerOptions());

        root!.Catalogs.Types.Should().Equal("Crm/Client", "Crm/Contact");
        AppDirectory.CatalogedTypesOf([root], [CatalogedTypeSet.Of("Thread")])
            .Types.Should().Equal("Crm/Client", "Crm/Contact", "Thread");
        CatalogedTypeSet.Of("A", "b").Should().Be(CatalogedTypeSet.Of("B", "a"), "a re-read of the same declaration compares equal");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<object> Views(UiControl control) =>
        ((System.Collections.IEnumerable)typeof(StackControl).BaseType!
            .GetProperty("Views", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(control)!)
        .Cast<object>().ToList();

    private static IEnumerable<object> Flatten(UiControl control) =>
        Views(control).SelectMany(v => v is StackControl s ? [v, .. Flatten(s)] : new[] { v });

    private static IReadOnlyList<MeshSearchControl> Sections(UiControl control) => [.. Views(control).OfType<MeshSearchControl>()];

    private static IReadOnlyList<string> SectionTitles(UiControl control) => [.. Sections(control).Select(s => s.Title!.ToString()!)];
}

/// <summary>
/// The type sections on a real mesh: the catalogued types come from the installed apps' declarations,
/// and the viewer's customization persists in their launcher arrangement and applies.
/// </summary>
public class HomeTypeSectionsMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A package root's own content, as the Store's <c>PluginContent</c> carries it.</summary>
    public sealed record CatalogingPackageContent
    {
        /// <summary>Whether the package is an app.</summary>
        public bool App { get; init; }
        /// <summary>The types the app catalogs.</summary>
        public IReadOnlyList<string>? Catalogs { get; init; }
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureHub(config => config.WithType<CatalogingPackageContent>(nameof(CatalogingPackageContent)))
            .AddMeshNodes(
                new MeshNode("SecCrm")
                {
                    NodeType = "Space",
                    Name = "SecCrm",
                    State = MeshNodeState.Active,
                    Content = new CatalogingPackageContent { App = true, Catalogs = ["SecCrm/Client"] },
                },
                new MeshNode("SecCrm", AppDirectory.InstallRecordsPartition) { NodeType = "Package", Name = "SecCrm", State = MeshNodeState.Active },
                // Not installed: its declaration does not count.
                new MeshNode("SecGone")
                {
                    NodeType = "Space",
                    Name = "SecGone",
                    State = MeshNodeState.Active,
                    Content = new CatalogingPackageContent { App = true, Catalogs = ["SecGone/Thing"] },
                })
            .AddAppCatalog("SecThreads", "Thread");

    [Fact(Timeout = 60000)]
    public async Task The_catalogued_types_are_what_the_installed_apps_declare()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Mesh.ServiceProvider.GetRequiredService<AppDirectory>();

        var types = await directory.CatalogedTypes()
            .Where(t => t.Contains("SecCrm/Client") && t.Contains("Thread"))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        types.Contains("SecGone/Thing").Should().BeFalse("a package that is not installed here catalogs nothing here");
    }

    [Fact(Timeout = 60000)]
    public async Task The_customization_persists_in_the_arrangement_and_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "sec-owner";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var source = Mesh.ServiceProvider.GetRequiredService<LauncherArrangementSource>();
        await access.RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(owner) with
        {
            NodeType = UserNodeType.NodeType,
            Name = owner,
            State = MeshNodeState.Active,
            Content = new User { FullName = owner },
        })).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var viewer = new AccessContext { ObjectId = owner, Name = owner };

        // No arrangement yet: the first change creates it.
        await access.RunAs(viewer, () => source.UpdateSections(owner, s => s.SetHidden("Markdown", true)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        // It exists now: the second change patches it and keeps the first.
        await access.RunAs(viewer, () => source.UpdateSections(owner, s => s with { Order = ["Space", "Slides/Deck"] }))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var stored = await source.Observe(owner)
            .Where(a => a is { Sections.Order.Count: 2 })
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        stored!.Sections.Hidden.Should().Equal("Markdown");

        var discovered = ImmutableList.Create(
            new HomeTypeSections.TypeSection("Markdown", 3, DateTimeOffset.UtcNow),
            new HomeTypeSections.TypeSection("Slides/Deck", 1, DateTimeOffset.UtcNow.AddMinutes(-5)),
            new HomeTypeSections.TypeSection("Space", 2, DateTimeOffset.UtcNow.AddMinutes(-9)));
        var arranged = HomeTypeSections.Arrange(discovered, stored.Sections, HomeTypeSections.ContentLegs(owner, null, null));
        arranged.Shown.Select(s => s.NodeType).Should().Equal("Space", "Slides/Deck");
        arranged.Hidden.Select(s => s.NodeType).Should().Equal("Markdown");
    }
}
