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
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The Admin app (<see cref="AdminAppNodeType"/>): <c>/Admin</c> is an app, not an empty Space; the
/// platform-admin surfaces live there and nowhere else; a person's own settings app holds only their
/// own things and opens for them alone; and nothing about it widens who can read <c>Admin/_Access</c>.
///
/// <para>Before: the Admin partition root had no node on a fresh mesh, so <c>/Admin</c> resolved to a
/// synthesized TYPELESS placeholder that activated on the default hub configuration and rendered an
/// empty page (and on a long-lived database it was a persisted <c>Space</c> row, listed among the
/// admin's workspaces); every administration tab rode the admin's OWN settings page.
/// <see cref="TheAdminRoot_IsTheAdminApp"/> and the tab tests fail on that code.</para>
///
/// <para>Identities are real grants, never the harness's public admin: <c>ConfigureMeshBase</c>, a
/// platform admin holding exactly the canonical shape (Admin on <c>Admin</c>), and two ordinary
/// users who hold only their own partitions — so a gate that leaked would show.</para>
/// </summary>
public class AdminAppTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PlatformAdmin = "adminapp-admin";
    private const string Ordinary = "adminapp-user";
    private const string Other = "adminapp-other";
    private const string Workspace = "AdminAppWorkspace";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            UserNode(PlatformAdmin), UserNode(Ordinary), UserNode(Other),
            // The canonical platform-admin shape: Admin on the Admin partition, nothing else.
            AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", AdminAppNodeType.Path),
            AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", PlatformAdmin),
            AssignmentNodeFactory.UserRole(Ordinary, "Admin", Ordinary),
            AssignmentNodeFactory.UserRole(Other, "Admin", Other),
            // A real workspace the admin can see — the positive control of the Spaces query.
            new MeshNode(Workspace) { Name = "Admin App Workspace", NodeType = "Space" },
            AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", Workspace),
            SeededAdminTab);

    /// <summary>A DATA-contributed Admin-app tab, seeded exactly as the platform seeds its own
    /// (Invitations, Updates, …): NodeSettings context, gated to the AdminApp type and AdminOnly.</summary>
    private static MeshNode SeededAdminTab { get; } = new("SeededProbe", "Admin/UiContribution")
    {
        NodeType = UiContributionNodeType.NodeType,
        Name = "Seeded probe",
        Content = new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = "SeededProbeArea",
            Label = "Seeded probe",
            Icon = "Mail",
            Group = AdminAppNodeType.PeopleGroup,
            GroupKey = AdminAppNodeType.PeopleGroupKey,
            GroupIcon = "People",
            Order = AdminAppNodeType.PeopleOrder + 10,
            Gates = new UiContributionGates { AdminOnly = true, NodeTypes = [AdminAppNodeType.NodeType] },
        },
    };

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private static MeshNode UserNode(string id) => new(id)
    {
        NodeType = UserNodeType.NodeType,
        Name = id,
        State = MeshNodeState.Active,
        Content = new User { FullName = id, Email = $"{id}@meshweaver.io" },
    };

    private void ActAs(string userId) => TestUsers.DevLogin(Mesh,
        new AccessContext { ObjectId = userId, Name = userId, Email = $"{userId}@meshweaver.io" });

    private Task<IReadOnlyList<MeshNode>> QueryAs(
        string userId, string query, CancellationToken ct, Func<IReadOnlyList<MeshNode>, bool>? until = null)
    {
        ActAs(userId);
        return Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Select(change => (IReadOnlyList<MeshNode>)change.Items.ToList())
            .Where(items => until is null || until(items))
            .FirstAsync()
            .Timeout(Budget)
            .Await(ct);
    }

    private IObservable<string> Render(string userId, string address, LayoutAreaReference reference)
    {
        ActAs(userId);
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(address), reference)
            .Select(change => change.Value.GetRawText())
            // Labels are localized ("Administrators"), so the page is matched case-blind.
            .Select(json => json.ToLowerInvariant());
    }

    private static LayoutAreaReference Settings(string? tab = null)
        => new(MeshNodeLayoutAreas.SettingsArea) { Id = tab };

    /// <summary>A settings-menu entry as it appears in the rendered JSON (a NavLink's title).</summary>
    private static string Tab(string label) => $"\"title\":\"{label}\"";

    // ── (a) Admin is not a Space ────────────────────────────────────────────────────────────

    /// <summary>
    /// The Admin partition root resolves to the Admin app. Before this change nothing registered a
    /// node at <c>Admin</c>, so the query below found nothing and <c>/Admin</c> was a typeless
    /// placeholder rendering an empty page. Read through the node stream — the authoritative read of
    /// one known path — never the eventually consistent query index.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TheAdminRoot_IsTheAdminApp()
    {
        ActAs(PlatformAdmin);
        var admin = await Mesh.GetWorkspace().GetMeshNodeStream(AdminAppNodeType.Path)
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

        admin.NodeType.Should().Be(AdminAppNodeType.NodeType, "the Admin root is an app, never a Space");
        admin.ExcludeFromContext.Should().Contain("content").And.Contain("search");
    }

    /// <summary>
    /// The workspace listing — the root leg the launcher's Spaces scope and the home content list are
    /// built on (<c>nodeType:Space</c> at the root) — does not list Admin for the one person who could
    /// read it, while it does list a real Space (the control: an empty answer would otherwise pass
    /// this for the wrong reason). A persisted legacy <c>Space</c> row at <c>Admin</c> is retyped by
    /// the database migration that ships with this change; here the type is the whole story.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TheSpacesList_OfAPlatformAdmin_DoesNotListAdmin()
    {
        var spaces = await QueryAs(PlatformAdmin, "namespace: nodeType:Space", TestContext.Current.CancellationToken,
            until: items => items.Any(n => n.Path == Workspace));

        spaces.Select(n => n.Path).Should().Contain(Workspace, "the control: a real Space is listed");
        spaces.Select(n => n.Path).Should().NotContain(AdminAppNodeType.Path);
    }

    // ── (b) /Admin is the Admin app for a platform admin ────────────────────────────────────

    [Fact(Timeout = 60000)]
    public async Task ThePlatformAdmin_FindsTheAdministrationSurfaces_InTheAdminApp()
    {
        var page = await Render(PlatformAdmin, AdminAppNodeType.Path, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains(Tab("administrators")) && json.Contains(Tab("data sources")),
                "a confirmed platform admin sees the Admin app's tabs",
                TestContext.Current.CancellationToken);

        page.Should().NotContain(Tab("metadata"), "node-management tabs mean nothing on the Admin root");
        // One Administrators tab — the Admin node's Access Control is part of it, not a second list.
        page.Should().NotContain(Tab("access control"));
        // Effective access is a per-NODE probe; on the instance it read as a global answer.
        page.Should().NotContain(Tab("check access"));
        // Who am I acts on the person: it is the person app's Account tab.
        page.Should().NotContain(Tab("who am i"));
        page.Should().NotContain(Tab("account"));
    }

    /// <summary>The app's sections: Administrators under People &amp; sign-in, Data sources under Operations.</summary>
    [Fact(Timeout = 60000)]
    public async Task TheAdminApp_IsSectioned_ByWhatItAdministers()
        => await Render(PlatformAdmin, AdminAppNodeType.Path, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains(Tab("people \\u0026 sign-in")) && json.Contains(Tab("operations")),
                "the tabs sit in the instance app's sections", TestContext.Current.CancellationToken);

    /// <summary>
    /// A DATA-contributed Admin-app tab (a <c>UiContribution</c> seed — how the platform ships
    /// Invitations, Privacy, Published to the web, Updates, Control lane and Inbox) shows in the
    /// app's nav, inside the SECTION its seed names.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ASeededAdminTab_ShowsInItsSection()
    {
        var page = await Render(PlatformAdmin, AdminAppNodeType.Path, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains(Tab("seeded probe")),
                "a data-contributed Admin-app tab is in the nav", TestContext.Current.CancellationToken);

        // In the People & sign-in NavGroup, not loose and not in another section: the group control
        // lists its entries' area ids, and the probe's NavLink is one of them.
        // The EntityStore's area keys are themselves JSON-encoded ids ("\"settings/2/1/…\""), so
        // each property name is decoded once more to get the area id.
        using var doc = JsonDocument.Parse(page);
        var areas = doc.RootElement.GetProperty("areas");
        string? probeArea = null, peopleGroup = null;
        foreach (var area in areas.EnumerateObject())
        {
            if (area.Value.TryGetProperty("title", out var t) && t.GetString() == "seeded probe")
                probeArea = JsonSerializer.Deserialize<string>(area.Name);
            if (area.Value.TryGetProperty("$type", out var type) && type.GetString() == "navgroupcontrol"
                && area.Value.GetProperty("title").GetString() == "people & sign-in")
                peopleGroup = JsonSerializer.Deserialize<string>(area.Name);
        }
        probeArea.Should().NotBeNull();
        peopleGroup.Should().NotBeNull("the People & sign-in section renders");
        probeArea!.Should().StartWith(peopleGroup + "/", "the seed names the People & sign-in section");
    }

    /// <summary>
    /// The seeded tab is in the FIRST rendered nav, not only eventually: an MCP
    /// <c>get @Admin/area/Settings</c> (<c>MeshOperations.RenderArea</c>) takes one frame and closes,
    /// and on a live instance that frame lacked every seeded Admin-app tab while the compiled ones
    /// were there. The contributed lane opened its admin verdict with a synthetic <c>false</c> (and
    /// the contribution catalog with an empty set), so the page — which waits for the viewer's
    /// permissions anyway — could paint before that lane had answered.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ASeededAdminTab_IsInTheFirstRenderedNav()
    {
        // The first frame whose nav carries the compiled Administrators tab is the frame a reader
        // that takes ONE frame sees; the seeded tab must be in it too, not in a later one.
        var nav = await Render(PlatformAdmin, AdminAppNodeType.Path, Settings())
            .Where(json => json.Contains(Tab("administrators")))
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
        nav.Should().Contain(Tab("seeded probe"),
            "the nav's first render carries the seeded Admin-app tab, not a nav painted before its lane answered");
    }

    /// <summary>
    /// 🚨 The production defect: the contributed lane resolved the viewer's platform-admin verdict
    /// when the tab stream was SUBSCRIBED, not when the page was rendered. On a distributed mesh the
    /// subscription runs off the viewer's delivery, where <see cref="AccessService.Context"/> is
    /// empty — so the viewer read as anonymous, <c>AdminOnly</c> never passed, and every seeded
    /// Admin-app tab was missing from the nav while the same seeds passed the same gates in the
    /// node menu (which resolves the viewer on the render turn). Pinned by building the tab stream
    /// with the viewer set and subscribing it with the viewer gone.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SeededAdminTabs_SurviveASubscriptionOffTheViewersDelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        // Activate the Admin hub the way a person does, so it is hosted.
        await Render(PlatformAdmin, AdminAppNodeType.Path, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains(Tab("administrators")), "the Admin app is up", ct);
        var hub = Mesh.GetHostedHub(new Address(AdminAppNodeType.Path), HostedHubCreation.Never);
        hub.Should().NotBeNull();

        var access = hub!.ServiceProvider.GetRequiredService<AccessService>();
        var host = new LayoutAreaHost(hub.GetWorkspace(), Settings(),
            hub.ServiceProvider.GetRequiredService<IUiControlService>(), null);
        access.SetContext(new AccessContext
            { ObjectId = PlatformAdmin, Name = PlatformAdmin, Email = $"{PlatformAdmin}@meshweaver.io" });
        IObservable<IReadOnlyList<SettingsMenuItemDefinition>> tabs;
        try
        {
            tabs = hub.Configuration.ObserveSettingsMenuItems(host, new RenderingContext(MeshNodeLayoutAreas.SettingsArea));
        }
        finally
        {
            // Off the delivery: no request context and — unlike this single-identity test host —
            // no process-wide fallback identity either, which is what a multi-user server has.
            access.SetContext(null);
            access.SetHostIdentity(null);
        }

        await tabs.Select(items => items.Select(i => i.Id).ToList())
            .Should().Within(Budget)
            .Match(ids => ids.Contains(SeededAdminTab.Id),
                "the viewer is resolved on the render turn, so the seeded tab passes AdminOnly", ct);
    }

    /// <summary>The Admin node's old Access Control link lands on the ONE Administrators tab.</summary>
    [Fact(Timeout = 60000)]
    public async Task TheAdminAccessControlLink_OpensAdministrators()
        => await Render(PlatformAdmin, AdminAppNodeType.Path, Settings("AccessControl"))
            .Should().Within(Budget)
            .Match(json => json.Contains(AdminAppNodeType.TabHref(GlobalAdministrationTab.TabId).ToLowerInvariant()),
                "the Admin node's grants ARE the administrators", TestContext.Current.CancellationToken);

    /// <summary>The old <c>/Admin/Settings/WhoAmI</c> lands on the viewer's own Account tab.</summary>
    [Fact(Timeout = 60000)]
    public async Task TheOldWhoAmILink_OpensThePersonsAccount()
        => await Render(PlatformAdmin, AdminAppNodeType.Path, Settings(WhoAmISettingsTab.TabId))
            .Should().Within(Budget)
            .Match(json => json.Contains(PersonApp.TabHref(PlatformAdmin, PersonApp.AccountTab).ToLowerInvariant()),
                "Who am I acts on the person, so it lives in the person app", TestContext.Current.CancellationToken);

    /// <summary><c>/Admin</c> with no area opens the app itself — the settings page of the Admin node.</summary>
    [Fact(Timeout = 60000)]
    public async Task TheAdminApp_IsTheDefaultPageOfAdmin()
        => await Render(PlatformAdmin, AdminAppNodeType.Path, new LayoutAreaReference(string.Empty))
            .Should().Within(Budget)
            .Match(json => json.Contains(Tab("administrators")),
                "/Admin lands on the Admin app", TestContext.Current.CancellationToken);

    // ── (c) nothing widens Admin/_Access ────────────────────────────────────────────────────

    /// <summary>
    /// A non-admin still reads NOTHING of the platform-admin grants; the platform admin reads them
    /// (the control — without it an empty answer proves nothing about the gate).
    ///
    /// <para>The grant asserted on is WRITTEN at runtime, as onboarding writes one: grants seeded
    /// through <c>AddMeshNodes</c> are image content served by the static provider, which this
    /// harness does not row-filter (a probe showed an ordinary user listing another user's static
    /// grant too), so a static grant cannot tell a gate from its absence. A persisted row is what a
    /// production <c>Admin/_Access</c> holds.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ANonAdmin_CannotReadAdminAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        const string grantee = "adminapp-second-admin";
        var grant = AssignmentNodeFactory.UserRole(grantee, "Admin", AdminAppNodeType.Path);
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => mesh.CreateNode(grant))
            .FirstAsync().Timeout(Budget).Await(ct);

        const string query = "namespace:Admin/_Access nodeType:AccessAssignment";
        var asAdmin = await QueryAs(PlatformAdmin, query, ct, until: items => items.Any(n => n.Path == grant.Path));
        asAdmin.Select(n => n.Path).Should().Contain(grant.Path, "the control: a platform admin reads the grant");

        var asOrdinary = await QueryAs(Ordinary, query, ct);
        asOrdinary.Select(n => n.Path).Should().NotContain(grant.Path,
            "the Admin app widens no read on the Admin partition");
    }

    [Fact(Timeout = 60000)]
    public async Task ANonAdmin_OpeningTheAdminApp_SeesNoAdministration()
        => await Render(Ordinary, AdminAppNodeType.Path, Settings())
            .Where(json => json.Contains(Tab("administrators")) || json.Contains(Tab("data sources")))
            .Should().NotEmit(TimeSpan.FromSeconds(8),
                "the Admin partition refuses a non-admin, and every admin tab waits for a positive admin verdict",
                TestContext.Current.CancellationToken);

    // ── the split: admin things in the Admin app, personal things in the settings app ──────

    /// <summary>
    /// A person's own settings app shows their Account tab and NONE of the administration tabs — for
    /// an ordinary user and, just as much, for a platform admin (whose own page used to carry them).
    /// </summary>
    [Theory(Timeout = 60000)]
    [InlineData(Ordinary)]
    [InlineData(PlatformAdmin)]
    public async Task OwnSettings_HoldOnlyPersonalTabs(string userId)
    {
        var page = await Render(userId, userId, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains("account") && json.Contains("preferences"),
                "the settings app renders the person's own tabs", TestContext.Current.CancellationToken);

        page.Should().NotContain(Tab("administrators"));
        page.Should().NotContain(Tab("data sources"));
    }

    [Fact(Timeout = 60000)]
    public async Task SomeoneElsesSettings_AreRefused()
    {
        var page = await Render(Ordinary, Other, Settings())
            .Should().Within(Budget)
            .Match(json => json.Contains("someone else"), "another person's settings are refused",
                TestContext.Current.CancellationToken);

        page.Should().NotContain("preferences");
    }

    /// <summary>
    /// The admin verdict every Admin-app tab and the "Who am I" grid are driven by is LIVE, never
    /// latched: a grant revoked while it is observed turns the verdict back to <c>false</c>, which
    /// removes the tab and turns the grid's Yes into No. The one-shot shape it replaced
    /// (<c>.Where(true).Take(1)</c>) completed on the first positive and never saw the revocation.
    ///
    /// <para>Asserted on the verdict stream rather than on a rendered page: once the grant is gone the
    /// viewer may no longer read the Admin node at all, so the page's own stream is refused — which
    /// is the partition doing its job, and not what this pins.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TheAdminVerdict_FollowsARevokedGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        const string revoked = "adminapp-revoked";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var grant = AssignmentNodeFactory.UserRole(revoked, "Admin", AdminAppNodeType.Path);
        await access.RunAsSystem(() => mesh.CreateNode(grant)).FirstAsync().Timeout(Budget).Await(ct);

        var verdict = AdminAppNodeType.LiveAdminVerdict(Mesh, revoked).Replay();
        using var connection = verdict.Connect();
        await verdict.Should().Within(Budget).Match(isAdmin => isAdmin, "the control: granted", ct);

        await access.RunAsSystem(() => mesh.DeleteNode(grant.Path!)).FirstAsync().Timeout(Budget).Await(ct);

        await verdict.Skip(1).Should().Within(Budget).Match(isAdmin => !isAdmin,
            "the verdict follows the revocation — it is not latched on the first positive", ct);
    }

    /// <summary>The "Who am I" rows are recomputed from each verdict: No is shown for No.</summary>
    [Fact]
    public void TheWhoAmIRows_ShowTheCurrentVerdict()
    {
        var session = new WhoAmISettingsTab.Session("u", "U", "u@x", false, null);
        string Admin(bool v) => WhoAmISettingsTab.Rows(session, v, Permission.None, Permission.None, k => k)
            .Single(r => r.Fact == "whoAmI.platformAdmin").Value;
        Admin(true).Should().Contain("whoAmI.yes");
        Admin(false).Should().Be("whoAmI.no");
    }

    /// <summary>An old link to an administration tab on the admin's own page redirects into the app.</summary>
    [Fact(Timeout = 60000)]
    public async Task AnOldAdministrationLink_RedirectsIntoTheAdminApp()
        => await Render(PlatformAdmin, PlatformAdmin, Settings(GlobalAdministrationTab.TabId))
            .Should().Within(Budget)
            .Match(json => json.Contains(AdminAppNodeType.TabHref(GlobalAdministrationTab.TabId).ToLowerInvariant()),
                "the relocated tab answers with a redirect into /Admin", TestContext.Current.CancellationToken);
}
