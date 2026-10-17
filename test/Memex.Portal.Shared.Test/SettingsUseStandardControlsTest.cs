using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The Settings pages are composed from the platform's standard controls only (maintainer,
/// 2026-10-17: <i>"pls use standard components everywhere"</i>). Every tab of the person app —
/// Profile, Account, Preferences, Notifications, API tokens, Security, Connected instances, Sharing,
/// Storage — and the menu around it, plus a Space's node settings (Metadata, Check access), render
/// as a frame in which NO area is an <see cref="HtmlControl"/> (hand-built markup, which
/// <c>Controls.Title</c> also emits) and no control carries the retired page-private classes
/// (<c>settings-menu-search</c>, <c>settings-section</c>).
///
/// <para>Read the way a person and an MCP <c>get @{user}/area/Settings/{tab}</c> read it: the
/// layout stream's frames, as the owner, through the layout client. A frame is judged once it has
/// SETTLED — the tab's nav link is active and every direct child of the content pane has rendered —
/// so a frame caught before its content cannot pass by being empty.</para>
/// </summary>
public class SettingsUseStandardControlsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Person = "stdcontrols-person";
    private const string Space = "StdControlsSpace";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private const string FailingTabId = "FailingProbe";

    /// <summary>A tab whose builder throws — the negative control for the failed-tab check.</summary>
    private static SettingsMenuItemDefinition FailingTab { get; } = new(
        Id: FailingTabId,
        Label: "Failing probe",
        ContentBuilder: (_, _, _) => throw new InvalidOperationException("probe tab failed on purpose"),
        Order: PersonApp.SharingOrder + 1,
        RequiredPermission: Permission.None);

    /// <summary>The page-private CSS classes the Settings page used to hand-roll its look with.</summary>
    private static readonly string[] RetiredClasses = ["settings-menu-search", "settings-section"];

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureServices(services => services
                .AddSingleton<ApiTokenService>()
                .AddSingleton<MeshWeaverInstanceService>())
            // The person-app tabs the portal registers on every node hub (MemexConfiguration).
            .ConfigureDefaultNodeHub(config => config
                .AddApiTokensSettingsTab()
                .AddStepUpSettingsTab()
                .AddInstancesSettingsTab()
                .AddNotificationsSettingsTab()
                .AddPersonAppTab(FailingTab))
            .AddMeshNodes(
                new MeshNode(Person)
                {
                    NodeType = UserNodeType.NodeType,
                    Name = "Standard Controls Person",
                    State = MeshNodeState.Active,
                    Content = new User { FullName = "Standard Controls Person", Email = $"{Person}@meshweaver.io" },
                },
                AssignmentNodeFactory.UserRole(Person, "Admin", Person),
                new MeshNode(Space) { Name = "Standard Controls Space", NodeType = "Space" },
                AssignmentNodeFactory.UserRole(Person, "Admin", Space));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private IObservable<JsonElement> Frames(string address, string? tab)
    {
        TestUsers.DevLogin(Mesh, new AccessContext
            { ObjectId = Person, Name = "Standard Controls Person", Email = $"{Person}@meshweaver.io" });
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(address),
                new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = tab })
            .Select(change => change.Value);
    }

    /// <summary>Every area of a frame: its id and its control.</summary>
    internal static IEnumerable<(string Id, JsonElement Control)> Areas(JsonElement frame)
        => frame.TryGetProperty("areas", out var areas) && areas.ValueKind == JsonValueKind.Object
            ? areas.EnumerateObject()
                .Where(a => a.Value.ValueKind == JsonValueKind.Object)
                .Select(a => (JsonSerializer.Deserialize<string>(a.Name) ?? a.Name, a.Value))
            : [];

    private static string? TypeOf(JsonElement control)
        => control.TryGetProperty("$type", out var t) ? t.GetString() : null;

    /// <summary>
    /// The non-standard parts of a frame: every area that is an <see cref="HtmlControl"/>, and every
    /// control carrying one of the retired page-private classes.
    /// </summary>
    internal static IReadOnlyList<string> NonStandard(JsonElement frame)
        => [.. Areas(frame).SelectMany(a =>
        {
            var found = new List<string>();
            if (TypeOf(a.Control) == nameof(HtmlControl))
                found.Add($"{a.Id}: HtmlControl");
            if (a.Control.TryGetProperty("class", out var cls) && cls.ValueKind == JsonValueKind.String
                && RetiredClasses.Any(r => cls.GetString()!.Split(' ').Contains(r)))
                found.Add($"{a.Id}: class '{cls.GetString()}'");
            return found;
        })];

    /// <summary>
    /// The tab-failure banners of a frame: <see cref="SettingsLayoutArea"/> catches a builder that
    /// throws and paints an Error <see cref="MessageBarControl"/> instead — a frame showing one has
    /// not rendered its tab, however standard the banner is.
    /// </summary>
    internal static IReadOnlyList<string> Failures(JsonElement frame)
        => [.. Areas(frame)
            .Where(a => TypeOf(a.Control) == nameof(MessageBarControl)
                && a.Control.TryGetProperty("intent", out var intent)
                && string.Equals(intent.GetString(), MessageBarIntent.Error, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Id)];

    /// <summary>
    /// Settled: the nav link of <paramref name="tab"/> is active, and the content pane and each of
    /// its direct children are in the frame.
    /// </summary>
    private static bool Settled(JsonElement frame, string tabUrlSuffix)
    {
        var areas = Areas(frame).ToDictionary(a => a.Id, a => a.Control);
        var active = areas.Values.Any(c => TypeOf(c) == nameof(NavLinkControl)
            && c.TryGetProperty("isActive", out var on) && on.ValueKind == JsonValueKind.True
            && c.TryGetProperty("url", out var url) && (url.GetString() ?? "").EndsWith(tabUrlSuffix, StringComparison.Ordinal));
        var pane = areas.FirstOrDefault(a => a.Key.EndsWith("/2", StringComparison.Ordinal)
            && a.Key.Count(ch => ch == '/') == 1 && TypeOf(a.Value) == nameof(StackControl));
        if (!active || pane.Value.ValueKind != JsonValueKind.Object
            || !pane.Value.TryGetProperty("areas", out var children) || children.GetArrayLength() == 0)
            return false;
        return children.EnumerateArray().All(child =>
            child.TryGetProperty("area", out var id) && areas.ContainsKey(id.GetString() ?? ""));
    }

    private async Task AssertStandard(string address, string tab, System.Threading.CancellationToken ct)
    {
        var frame = await Frames(address, tab)
            .Should().Within(Budget)
            .Match(f => Settled(f, $"/Settings/{tab}"), $"the '{tab}' tab of {address}'s settings renders", ct);

        Failures(frame).Should().BeEmpty($"the '{tab}' tab rendered rather than failing");
        NonStandard(frame).Should().BeEmpty(
            $"the '{tab}' tab and the menu around it are composed from the platform's standard controls only");
        Areas(frame).Should().Contain(a => TypeOf(a.Control) == nameof(TextFieldControl)
                && a.Control.TryGetProperty("placeholder", out var p) && p.GetString() == "Search settings…",
            "the menu search is the platform's text field");
    }

    [Theory(Timeout = 120000)]
    [InlineData(PersonApp.ProfileTab)]
    [InlineData(WhoAmISettingsTab.AccountTabId)]
    [InlineData(PersonApp.PreferencesTab)]
    [InlineData(NotificationsSettingsTab.TabId)]
    [InlineData(ApiTokensSettingsTab.TabId)]
    [InlineData(StepUpSettingsTab.TabId)]
    [InlineData(InstancesSettingsTab.TabId)]
    [InlineData(PersonApp.SharingTab)]
    [InlineData(MeshWeaver.Graph.Storage.StorageSettingsTab.TabId)]
    public Task EveryPersonAppTab_IsComposedOfStandardControls(string tab) => AssertStandard(Person, tab, TestContext.Current.CancellationToken);

    [Theory(Timeout = 120000)]
    [InlineData("Metadata")]
    [InlineData("EffectiveAccess")]
    public Task ASpacesNodeSettings_AreComposedOfStandardControls(string tab) => AssertStandard(Space, tab, TestContext.Current.CancellationToken);

    /// <summary>
    /// The negative control for <see cref="Failures"/>: a tab whose builder throws settles with an
    /// Error banner, and the check sees it — so a green tab above is a tab that rendered.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AFailedTab_IsDetected_NotPassedAsStandard()
    {
        var frame = await Frames(Person, FailingTabId)
            .Should().Within(Budget)
            .Match(f => Settled(f, $"/Settings/{FailingTabId}"), "the failing probe tab settles",
                TestContext.Current.CancellationToken);

        Failures(frame).Should().NotBeEmpty("the builder threw, so the pane holds the failure banner");
    }

    /// <summary>
    /// The Profile tab EMBEDS the profile editor (<c>EditProfile</c>) through a
    /// <see cref="LayoutAreaControl"/>, which runs on its own layout stream — so the Settings frame
    /// alone does not see inside it. Read that stream too, settled on its own sections.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TheEmbeddedProfileEditor_IsComposedOfStandardControls()
    {
        TestUsers.DevLogin(Mesh, new AccessContext
            { ObjectId = Person, Name = "Standard Controls Person", Email = $"{Person}@meshweaver.io" });
        var frame = await GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(Person),
                new LayoutAreaReference(UserActivityLayoutAreas.EditProfileArea))
            .Select(change => change.Value)
            .Should().Within(Budget)
            .Match(f => Areas(f).Any(a => TypeOf(a.Control) == nameof(MeshNodeContentEditorControl))
                    && Areas(f).Count(a => TypeOf(a.Control) == nameof(MarkdownEditorControl)) >= 2,
                "the profile editor renders its sections", TestContext.Current.CancellationToken);

        NonStandard(frame).Should().BeEmpty("the embedded profile editor is standard controls only");
    }

    /// <summary>
    /// The API tokens and Connected instances forms are platform cards (<see cref="CardSkin"/>), not
    /// inline-styled boxes.
    /// </summary>
    [Theory(Timeout = 120000)]
    [InlineData(ApiTokensSettingsTab.TabId)]
    [InlineData(InstancesSettingsTab.TabId)]
    public async Task TheCreateForms_AreCards(string tab)
    {
        var frame = await Frames(Person, tab)
            .Should().Within(Budget)
            .Match(f => Settled(f, $"/Settings/{tab}"), $"the '{tab}' tab renders",
                TestContext.Current.CancellationToken);

        Areas(frame).Should().Contain(a => a.Control.TryGetProperty("skins", out var skins)
                && skins.ValueKind == JsonValueKind.Array
                && skins.EnumerateArray().Any(s => s.TryGetProperty("$type", out var t) && t.GetString() == nameof(CardSkin)),
            "the form is a section on the platform's card skin");
        Areas(frame).Should().NotContain(a => a.Control.TryGetProperty("style", out var st)
                && st.ValueKind == JsonValueKind.String && st.GetString()!.Contains("border-radius"),
            "no control emulates a card with inline CSS");
    }

    /// <summary>The detector itself: a frame that DOES hold hand-built markup or a retired class is flagged.</summary>
    [Fact]
    public void TheDetector_FlagsHtmlAndRetiredClasses_AndNothingElse()
    {
        var frame = JsonDocument.Parse("""
            {"areas":{
              "\"a\"":{"$type":"HtmlControl","data":"<p>x</p>"},
              "\"b\"":{"$type":"TextFieldControl","class":"settings-menu-search"},
              "\"c\"":{"$type":"StackControl","class":"settings-content-pane"},
              "\"d\"":{"$type":"LabelControl","data":"Account"}
            }}
            """).RootElement;

        Assert.Equal(["a: HtmlControl", "b: class 'settings-menu-search'"], NonStandard(frame));
    }

    /// <summary>The Check access verdict rows: one per checked permission, Allowed exactly where granted.</summary>
    [Fact]
    public void ThePermissionRows_ReadTheVerdict()
    {
        var rows = SettingsLayoutArea.PermissionRows(Permission.Read | Permission.Update, "Allowed", "Denied");

        rows.Select(r => r.Permission).Should().Equal(SettingsLayoutArea.CheckedPermissions.Select(p => p.ToString()));
        rows.Where(r => r.Status == "Allowed").Select(r => r.Permission).Should().Equal("Read", "Update");
        rows.Where(r => r.Status == "Denied").Select(r => r.Permission).Should().Equal("Create", "Delete", "Comment");
    }
}
