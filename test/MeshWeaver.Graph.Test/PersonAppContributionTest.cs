using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The DATA-contributed person-app tab (<see cref="UiContribution.PersonAppContext"/>) — the surface
/// an IN-APP EXTENSION appears on inside the viewer's own settings app
/// (<c>Doc/Architecture/InAppExtensions</c>). Pinned end to end through the layout client:
/// <list type="bullet">
/// <item>a contribution whose embedded address the viewer can READ is a tab of their person app;</item>
/// <item>one whose address they cannot read (<see cref="UiContributionGates.RequireAddressAccess"/> —
/// an extension they have not acquired) is not;</item>
/// <item>neither appears on a Space's settings page;</item>
/// <item>an address outside the contribution's own partition is dropped.</item>
/// </list>
/// <para>Real grants only (<c>ConfigureMeshBase</c>): the harness's public admin would make every
/// address readable and the access gate unfalsifiable.</para>
/// </summary>
public class PersonAppContributionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Person = "iae-person";
    private const string Space = "IaeSpace";
    private const string OtherPerson = "iae-other";
    private const string Held = "IaeHeld";
    private const string NotHeld = "IaeNotHeld";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private static MeshNode ExtensionTab(string partition, string label, bool gated = true, string id = "Tab") =>
        new(id, partition + "/Tabs")
        {
            Name = label,
            NodeType = UiContributionNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new UiContribution
            {
                Context = UiContribution.PersonAppContext,
                Address = partition,
                Area = "Overview",
                Label = label,
                Order = 90,
                RequiredPermission = Permission.Update,
                Gates = gated ? new UiContributionGates { RequireAddressAccess = true } : null,
            },
        };

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Person)
                {
                    NodeType = UserNodeType.NodeType,
                    Name = "Extension Person",
                    State = MeshNodeState.Active,
                    Content = new User { FullName = "Extension Person", Email = $"{Person}@meshweaver.io" },
                },
                AssignmentNodeFactory.UserRole(Person, "Admin", Person),
                new MeshNode(OtherPerson)
                {
                    NodeType = UserNodeType.NodeType,
                    Name = "Other Person",
                    State = MeshNodeState.Active,
                    Content = new User { FullName = "Other Person", Email = $"{OtherPerson}@meshweaver.io" },
                },
                // The person may UPDATE the other person's root — yet its settings are not their person app.
                AssignmentNodeFactory.UserRole(Person, "Admin", OtherPerson),
                new MeshNode(Space) { Name = "Iae Space", NodeType = "Space" },
                AssignmentNodeFactory.UserRole(Person, "Admin", Space),
                // An extension the person HOLDS (a Viewer grant — what acquiring a package mints).
                new MeshNode(Held) { Name = "Held package", NodeType = "Space" },
                AssignmentNodeFactory.UserRole(Person, "Viewer", Held),
                ExtensionTab(Held, "Held extension"),
                // One they do NOT hold: no grant on its partition.
                new MeshNode(NotHeld) { Name = "Unheld package", NodeType = "Space" },
                ExtensionTab(NotHeld, "Unheld extension"),
                // The CONTROL: the same partition, projected in the same pass, but ungated — once it
                // is on the page the catalog has been projected, so the gated tab's absence is the
                // gate's verdict and not a frame caught early.
                ExtensionTab(NotHeld, "Ungated control", gated: false, id: "Control"),
                // One pointing OUTSIDE its own partition — dropped whatever the viewer holds.
                new MeshNode("Foreign", Held + "/Tabs")
                {
                    Name = "Foreign extension",
                    NodeType = UiContributionNodeType.NodeType,
                    State = MeshNodeState.Active,
                    Content = new UiContribution
                    {
                        Context = UiContribution.PersonAppContext,
                        Address = Space,
                        Area = "Overview",
                        Label = "Foreign extension",
                        RequiredPermission = Permission.Update,
                    },
                });

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private IObservable<string> Render(string address, LayoutAreaReference reference)
    {
        TestUsers.DevLogin(Mesh,
            new AccessContext { ObjectId = Person, Name = "Extension Person", Email = $"{Person}@meshweaver.io" });
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(address), reference)
            .Select(change => change.Value.GetRawText().ToLowerInvariant());
    }

    private static LayoutAreaReference Settings(string? tab = null)
        => new(MeshNodeLayoutAreas.SettingsArea) { Id = tab };

    private static string Tab(string label) => $"\"title\":\"{label}\"";

    [Fact(Timeout = 60000)]
    public async Task AnExtensionTheViewerHolds_IsATabOfTheirPersonApp_AndOneTheyDoNotHold_IsNot()
    {
        var page = await Render(Person, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains(Tab("held extension")) && json.Contains(Tab("ungated control")),
                "a PersonApp contribution whose address the viewer can read is a tab of their person app",
                TestContext.Current.CancellationToken);

        page.Should().NotContain(Tab("unheld extension"),
            "RequireAddressAccess hides the tab of an extension the viewer has not acquired");
        page.Should().NotContain(Tab("foreign extension"),
            "an embedded address outside the contribution's own partition is dropped");
    }

    [Fact(Timeout = 60000)]
    public async Task APersonAppContribution_IsNotOnASpacesSettings()
    {
        // The control: the Space's page has rendered its node tabs, so the absence is a verdict.
        var page = await Render(Space, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains(Tab("metadata")), "the Space's settings render",
                TestContext.Current.CancellationToken);

        page.Should().NotContain(Tab("held extension"),
            "a person-app tab never appears on a Space's settings page");
    }

    [Fact(Timeout = 60000)]
    public async Task APersonAppContribution_IsNotOnAnotherPersonsSettings_EvenWithUpdateThere()
    {
        // IsPersonAppHub is owner-scoped: another user's root is not the viewer's person app, even
        // when the viewer may change it — that page says whose settings they are and renders no
        // tabs. The control is that sentence, so the absence below is a verdict, not an early frame.
        var page = await Render(OtherPerson, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains("someone else"), "another person's settings say whose they are",
                TestContext.Current.CancellationToken);

        page.Should().NotContain(Tab("held extension"),
            "the viewer's extension tabs belong to their OWN settings app only");
        page.Should().NotContain(Tab("ungated control"),
            "no person-app contribution renders on another person's root");
    }

    // ── the pure projection ────────────────────────────────────────────────────────────────

    private static (MeshNode, UiContribution) Contribution(string partition, string id, UiContribution content) =>
        (new MeshNode(id, partition + "/Tabs") { Name = id, NodeType = UiContributionNodeType.NodeType }, content);

    private static readonly MeshNode UserNode = new(Person) { NodeType = UserNodeType.NodeType, Content = new User() };

    [Fact]
    public void Projection_EmbedsTheDeclaredArea_AndReturnsTheAddressToProbe()
    {
        var tabs = UiContributionProjection.ProjectPersonAppTabs(
            [Contribution("Signature", "SigningAuthority", new UiContribution
            {
                Context = UiContribution.PersonAppContext,
                Address = "Signature/Workspace",
                Area = "SigningAuthority",
                Label = "Signing authority",
                LabelKey = "personApp.signingAuthority",
                Order = 80,
                RequiredPermission = Permission.Update,
                Keywords = ["signature", "swisscom"],
                Gates = new UiContributionGates { RequireAddressAccess = true },
            })],
            Person, UserNode, isAdmin: false, viewerId: Person);

        var (tab, probe) = tabs.Should().ContainSingle().Subject;
        tab.Id.Should().Be("SigningAuthority", "the contribution node's id is the stable tab id");
        tab.LabelKey.Should().Be("personApp.signingAuthority");
        tab.Order.Should().Be(80);
        tab.RequiredPermission.Should().Be(Permission.Update);
        tab.Keywords.Should().Equal("signature", "swisscom");
        probe.Should().Be("Signature/Workspace", "RequireAddressAccess probes the embedded address");
    }

    [Fact]
    public void Projection_OnlyThePersonAppContext_InItsOwnPartition_WithoutAProbeUnlessAsked()
    {
        var tabs = UiContributionProjection.ProjectPersonAppTabs(
            [
                Contribution("P", "profile", new UiContribution { Context = UiContribution.ProfileContext, Area = "A" }),
                Contribution("P", "nodeSettings", new UiContribution { Context = UiContribution.NodeSettingsContext, Area = "A" }),
                Contribution("P", "noArea", new UiContribution { Context = UiContribution.PersonAppContext }),
                Contribution("P", "foreign", new UiContribution
                    { Context = UiContribution.PersonAppContext, Address = "Q", Area = "A" }),
                Contribution("P", "admin", new UiContribution
                {
                    Context = UiContribution.PersonAppContext, Area = "A",
                    Gates = new UiContributionGates { AdminOnly = true },
                }),
                Contribution("P", "ok", new UiContribution
                    { Context = UiContribution.PersonAppContext, Address = "P/Hub", Area = "A" }),
            ],
            Person, UserNode, isAdmin: false, viewerId: Person);

        var (tab, probe) = tabs.Should().ContainSingle().Subject;
        tab.Id.Should().Be("ok");
        tab.RequiredPermission.Should().Be(Permission.Read, "a contribution never demands less than Read");
        probe.Should().BeNull("no RequireAddressAccess gate, so nothing to probe");
    }

    private static SettingsMenuItemDefinition PlainTab(string id) =>
        new(id, id, (_, stack, _) => stack);

    [Fact]
    public void AddressAccess_ShowsOnlyTheTabsWhoseAddressTheViewerCanRead()
    {
        var outcomes = new Dictionary<string, IObservable<PermissionCheckOutcome>>
        {
            ["Held"] = Observable.Return(PermissionCheckOutcome.Granted),
            ["NotHeld"] = Observable.Return(PermissionCheckOutcome.Denied),
            ["Pending"] = Observable.Never<PermissionCheckOutcome>(),
            ["Undetermined"] = Observable.Return(PermissionCheckOutcome.Undetermined("fold faulted: probe")),
        };
        var undetermined = new List<(string Address, string Reason)>();
        string[]? shown = null;
        // Every probe here answers synchronously (or never), so the first frame is emitted during
        // Subscribe — collected on the calling thread, no blocking bridge.
        using var subscription = SettingsMenuItemsExtensions.ApplyAddressAccess(
            [
                (PlainTab("held"), "Held"),
                (PlainTab("notHeld"), "NotHeld"),
                (PlainTab("pending"), "Pending"),
                (PlainTab("undetermined"), "Undetermined"),
                (PlainTab("unprobed"), null),
            ],
            address => outcomes[address],
            (address, reason) => undetermined.Add((address, reason)))
            .Select(tabs => tabs.Select(t => t.Id).ToArray())
            .Subscribe(ids => shown ??= ids);

        shown.Should().Equal(["held", "unprobed"],
            "a tab shows only on a GRANTED verdict; denied, pending and undetermined all hide it");
        undetermined.Should().ContainSingle().Which.Should().Be(("Undetermined", "fold faulted: probe"),
            "an undetermined verdict is REPORTED (a degraded dependency), never read as 'not held' in silence");
    }

    [Fact]
    public void AddressAccess_FollowsTheLiveVerdict_AcquiringShowsTheTab_RevokingHidesItAgain()
    {
        var verdict = new System.Reactive.Subjects.BehaviorSubject<PermissionCheckOutcome>(PermissionCheckOutcome.Denied);
        var frames = new List<string[]>();
        using var _ = SettingsMenuItemsExtensions.ApplyAddressAccess(
                [(PlainTab("extension"), "Ext")], _ => verdict)
            .Subscribe(tabs => frames.Add(tabs.Select(t => t.Id).ToArray()));

        verdict.OnNext(PermissionCheckOutcome.Granted);
        verdict.OnNext(PermissionCheckOutcome.Denied);

        frames.Should().HaveCount(3, "one frame for not held, one for acquired, one for revoked");
        frames[0].Should().BeEmpty();
        frames[1].Should().Equal("extension");
        frames[2].Should().BeEmpty();
    }

    [Fact]
    public void AContributedTab_NeverShadowsATabAlreadyOnThePage()
    {
        var kept = SettingsMenuItemsExtensions.WithoutShadowingTabs(
            [PlainTab(PersonApp.SharingTab), PlainTab(PersonApp.PreferencesTab)],
            [PlainTab("sharing"), PlainTab("SigningAuthority"), PlainTab("SigningAuthority")]);

        kept.Select(t => t.Id).Should().Equal(["SigningAuthority"],
            "a contributed 'sharing' cannot replace the built-in Sharing (case-insensitive), and a duplicate contribution lands once");
    }

    [Fact]
    public void AddressAccess_IsAGrantedVerdictOnTheEmbeddedAddress()
    {
        SettingsMenuItemsExtensions.PassesAddressAccess(PermissionCheckOutcome.Granted).Should().BeTrue();
        SettingsMenuItemsExtensions.PassesAddressAccess(PermissionCheckOutcome.Denied).Should().BeFalse();
        SettingsMenuItemsExtensions.PassesAddressAccess(PermissionCheckOutcome.Undetermined("x")).Should().BeFalse(
            "no verdict is not a grant — fail closed");
    }

    [Fact]
    public void SeedValidation_KnowsThePersonAppContext_AndReportsAnInertGateAndAForeignAddress()
    {
        UiContributionSeedValidation.PlatformContexts.Should().Contain(UiContribution.PersonAppContext);

        var problems = UiContributionSeedValidation.Validate(
        [
            new MeshNode("inert", "P/Tabs")
            {
                NodeType = UiContributionNodeType.NodeType,
                Content = new UiContribution
                {
                    Context = UiContribution.PersonAppContext, Area = "A",
                    Gates = new UiContributionGates { RequireAddressAccess = true },
                },
            },
            new MeshNode("foreign", "P/Tabs")
            {
                NodeType = UiContributionNodeType.NodeType,
                Content = new UiContribution { Context = UiContribution.PersonAppContext, Address = "Q", Area = "A" },
            },
        ]);

        problems.Should().Contain(p => p.Contains("P/Tabs/inert") && p.Contains("RequireAddressAccess"));
        problems.Should().Contain(p => p.Contains("P/Tabs/foreign") && p.Contains("outside"));

        UiContributionSeedValidation.Validate(
        [
            new MeshNode("Sharing", "P/Tabs")
            {
                NodeType = UiContributionNodeType.NodeType,
                Content = new UiContribution { Context = UiContribution.PersonAppContext, Area = "A" },
            },
        ]).Should().Contain(p => p.Contains("P/Tabs/Sharing") && p.Contains("collides"),
            "a contribution named like a built-in person-app tab is reported");

        UiContributionSeedValidation.Validate(
        [
            new MeshNode("profileGate", "P/Tabs")
            {
                NodeType = UiContributionNodeType.NodeType,
                Content = new UiContribution
                {
                    Context = UiContribution.ProfileContext, Address = "P", Area = "A",
                    Gates = new UiContributionGates { RequireAddressAccess = true },
                },
            },
        ]).Should().Contain(p => p.Contains("P/Tabs/profileGate") && p.Contains("inert"),
            "the gate on a Profile section is inert and reported");
    }
}
