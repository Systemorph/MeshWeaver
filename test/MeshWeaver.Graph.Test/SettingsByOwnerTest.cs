using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Settings by OWNER: every settings tab lives in the app of the thing it changes — the instance
/// (<c>/Admin</c>), the person (<c>/{user}/Settings</c>, <see cref="PersonApp"/>) or a node (its ⋯ →
/// Settings…). Pinned end to end through the layout client, as a person sees the pages:
/// <list type="bullet">
/// <item>a tab registered for the PERSON rides every node hub, yet shows on the viewer's own settings
/// page only — never on a Space's — and a Space's link to it redirects into the person app;</item>
/// <item>a Space-root tab (Node types, Groups) shows on the Space ROOT only, not on every node below
/// it, and a descendant's link to it redirects to the root;</item>
/// <item>the person app is the person's things, with node management hidden;</item>
/// <item>the node's ⋯ menu offers "Settings…".</item>
/// </list>
/// <para>Before: the personal tabs (API tokens, Instances, Notifications) were registered on EVERY
/// node hub with <see cref="Permission.None"/> and appeared on every Space's settings page; the
/// Space-management tabs appeared on every descendant. <see cref="APersonalTab_IsNotOnASpacesSettings"/>
/// and <see cref="SpaceRootTabs_AreNotOnADescendant"/> fail on that code.</para>
/// </summary>
public class SettingsByOwnerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Person = "owner-person";
    private const string OtherPerson = "owner-other";
    private const string Space = "OwnerSpace";
    private const string Page = Space + "/Page";
    private const string ProbeTabId = "PersonalProbe";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    /// <summary>A personal tab, registered the way a module registers one — on every node hub.</summary>
    private static SettingsMenuItemDefinition ProbeTab { get; } = new(
        Id: ProbeTabId,
        Label: "Personal probe",
        ContentBuilder: (_, stack, _) => stack.WithView(Controls.Markdown("personal probe content")),
        Order: PersonApp.NotificationsOrder,
        RequiredPermission: Permission.None);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureDefaultNodeHub(config => config.AddPersonAppTab(ProbeTab))
            .AddMeshNodes(
                new MeshNode(Person)
                {
                    NodeType = UserNodeType.NodeType,
                    Name = "Owner Person",
                    State = MeshNodeState.Active,
                    Content = new User { FullName = "Owner Person", Email = $"{Person}@meshweaver.io" },
                },
                AssignmentNodeFactory.UserRole(Person, "Admin", Person),
                new MeshNode(OtherPerson)
                {
                    NodeType = UserNodeType.NodeType,
                    Name = "Other Person",
                    State = MeshNodeState.Active,
                    Content = new User { FullName = "Other Person", Email = $"{OtherPerson}@meshweaver.io" },
                },
                // Person may UPDATE the other person's root — yet its person app is not theirs.
                AssignmentNodeFactory.UserRole(Person, "Admin", OtherPerson),
                new MeshNode(Space) { Name = "Owner Space", NodeType = "Space" },
                new MeshNode("Page", Space) { Name = "A Page" },
                AssignmentNodeFactory.UserRole(Person, "Admin", Space));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition));

    private void ActAsPerson() => TestUsers.DevLogin(Mesh,
        new AccessContext { ObjectId = Person, Name = "Owner Person", Email = $"{Person}@meshweaver.io" });

    private IObservable<string> Render(string address, LayoutAreaReference reference)
    {
        ActAsPerson();
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(address), reference)
            .Select(change => change.Value.GetRawText().ToLowerInvariant());
    }

    private static LayoutAreaReference Settings(string? tab = null)
        => new(MeshNodeLayoutAreas.SettingsArea) { Id = tab };

    private static string Tab(string label) => $"\"title\":\"{label}\"";

    // ── personal tabs: the person app only ──────────────────────────────────────────────────

    [Fact(Timeout = 60000)]
    public async Task APersonalTab_IsInThePersonApp()
        => await Render(Person, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains(Tab("personal probe")),
                "a tab registered through AddPersonAppTab shows on the viewer's own settings page",
                TestContext.Current.CancellationToken);

    [Fact(Timeout = 60000)]
    public async Task APersonalTab_IsNotOnASpacesSettings()
    {
        // The control: the Space's page has rendered its node tabs — the absence below is a verdict,
        // not a page caught before its menu.
        var page = await Render(Space, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains(Tab("metadata")), "the Space's settings render",
                TestContext.Current.CancellationToken);

        page.Should().NotContain(Tab("personal probe"),
            "a tab acting on the signed-in person never appears on a Space's settings page");
    }

    [Fact(Timeout = 60000)]
    public async Task APersonalTabLink_OnASpace_RedirectsIntoThePersonApp()
        => await Render(Space, Settings(ProbeTabId))
            .Should().Within(Budget)
            .Match(json => json.Contains(PersonApp.TabHref(Person, ProbeTabId).ToLowerInvariant()),
                "an old link answers with a redirect to the viewer's own person app",
                TestContext.Current.CancellationToken);

    // ── the person app is the person's things ───────────────────────────────────────────────

    [Fact(Timeout = 60000)]
    public async Task ThePersonApp_HoldsThePersonsTabs_AndNoNodeManagement()
    {
        var page = await Render(Person, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains(Tab("profile")) && json.Contains(Tab("account"))
                           && json.Contains(Tab("preferences")) && json.Contains(Tab("sharing")),
                "Profile, Account, Preferences and Sharing are the person app's own tabs",
                TestContext.Current.CancellationToken);

        foreach (var nodeTab in new[] { "metadata", "node types", "groups", "check access", "access control", "appearance" })
            page.Should().NotContain(Tab(nodeTab), $"'{nodeTab}' is node management, not the person's");
    }

    /// <summary>The theme moved into Preferences; the old Appearance link lands there.</summary>
    [Fact(Timeout = 60000)]
    public async Task TheOldAppearanceLink_OpensPreferences()
        => await Render(Person, Settings("Appearance"))
            .Should().Within(Budget)
            .Match(json => json.Contains(PersonApp.TabHref(Person, PersonApp.PreferencesTab).ToLowerInvariant()),
                "Appearance is part of Preferences now", TestContext.Current.CancellationToken);

    // ── Space-root tabs: the root only ──────────────────────────────────────────────────────

    [Fact(Timeout = 60000)]
    public async Task SpaceRootTabs_AreOnTheRoot()
        => await Render(Space, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains(Tab("node types")) && json.Contains(Tab("groups")),
                "the Space root carries its own management", TestContext.Current.CancellationToken);

    [Fact(Timeout = 60000)]
    public async Task SpaceRootTabs_AreNotOnADescendant()
    {
        var page = await Render(Page, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains(Tab("metadata")) && json.Contains(Tab("check access")),
                "the descendant's own node settings render", TestContext.Current.CancellationToken);

        page.Should().NotContain(Tab("node types"), "a Space's node types are managed on the Space root");
        page.Should().NotContain(Tab("groups"), "a Space's groups are managed on the Space root");
        page.Should().NotContain(Tab("files"), "Files is the ⋯ menu's, not a settings tab");
        page.Should().NotContain(Tab("appearance"), "the theme is the viewer's preference, not the node's");
    }

    [Fact(Timeout = 60000)]
    public async Task ASpaceRootTabLink_OnADescendant_RedirectsToTheRoot()
        => await Render(Page, Settings("Groups"))
            .Should().Within(Budget)
            .Match(json => json.Contains($"/{Space}/Settings/Groups".ToLowerInvariant()),
                "the descendant's link lands on the root's tab", TestContext.Current.CancellationToken);

    // ── node settings from the ⋯ menu ───────────────────────────────────────────────────────

    private IObservable<IReadOnlyList<NodeMenuItemDefinition>> NodeMenu(string address)
    {
        ActAsPerson();
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(address), new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea))
            .GetControlStream(MenuControl.GetMenuArea(NodeMenuItemsExtensions.NodeMenuContext))
            .OfType<MenuControl>()
            .Select(x => (IReadOnlyList<NodeMenuItemDefinition>)x.Items);
    }

    [Fact(Timeout = 60000)]
    public async Task TheNodeMenu_OffersSettings()
    {
        var items = await NodeMenu(Page)
            .Where(i => i.Any(m => m.Area == MeshNodeLayoutAreas.SettingsArea))
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

        var settings = items.Single(m => m.Area == MeshNodeLayoutAreas.SettingsArea);
        settings.Href.Should().Be(MeshNodeLayoutAreas.BuildUrl(Page, MeshNodeLayoutAreas.SettingsArea),
            "Settings… opens THIS node's settings");
    }

    /// <summary>
    /// On a person's root, Settings… is the person app — which opens for its OWNER only. A viewer who
    /// merely holds Update there (an Admin grant) is not offered an entry that would land on a
    /// refusal; the owner is (the positive control is <see cref="TheOwnersMenu_OffersTheirSettings"/>).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SomeoneElsesRoot_OffersNoSettings()
    {
        // The control: the menu has rendered for a viewer holding Update (Recycle needs it).
        var items = await NodeMenu(OtherPerson)
            .Where(i => i.Any(m => m.Area == MeshNodeLayoutAreas.RecycleArea))
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

        items.Should().NotContain(m => m.Area == MeshNodeLayoutAreas.SettingsArea,
            "another person's settings page refuses this viewer, so the menu does not offer it");
    }

    [Fact(Timeout = 60000)]
    public async Task TheOwnersMenu_OffersTheirSettings()
    {
        var items = await NodeMenu(Person)
            .Where(i => i.Any(m => m.Area == MeshNodeLayoutAreas.RecycleArea))
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

        items.Select(m => m.Area).Should().Contain(MeshNodeLayoutAreas.SettingsArea,
            "the owner's ⋯ opens their own person app");
    }

    // ── pure pieces ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://memex.systemorph.com", null, "memex.systemorph.com")]
    [InlineData(null, "https://memex.meshweaver.cloud/", "memex.meshweaver.cloud")]
    [InlineData("http://localhost:5022", null, "localhost:5022")]
    [InlineData(null, null, null)]
    public void TheInstanceName_IsThePublicHost(string? portalBaseUrl, string? publicBaseUrl, string? expected)
    {
        var values = new Dictionary<string, string?>();
        if (portalBaseUrl is not null) values["Portal:BaseUrl"] = portalBaseUrl;
        if (publicBaseUrl is not null) values["PublicBaseUrl"] = publicBaseUrl;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        AdminAppNodeType.InstanceName(configuration).Should().Be(expected);
    }

    [Fact]
    public void ThePersonAppHref_IsBuiltLikeItsTabLinks()
        => PersonApp.TabHref("alice", "Profile").Should().StartWith(PersonApp.Href("alice") + "/");

    [Theory]
    [InlineData("alice", "alice", true)]
    [InlineData("Alice", "alice", true)]
    [InlineData("bob", "alice", false)]
    [InlineData("alice/Threads", "alice", false)]
    [InlineData("alice", null, false)]
    public void ThePersonApp_IsTheViewersOwnRoot(string hubPath, string? viewer, bool expected)
        => PersonApp.IsOwnRoot(hubPath, viewer).Should().Be(expected);
}
