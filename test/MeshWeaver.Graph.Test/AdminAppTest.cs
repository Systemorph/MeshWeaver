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
            AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", Workspace));

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
            // Labels are localized ("Global administration"), so the page is matched case-blind.
            .Select(json => json.ToLowerInvariant());
    }

    private static LayoutAreaReference Settings(string? tab = null)
        => new(MeshNodeLayoutAreas.SettingsArea) { Id = tab };

    // ── (a) Admin is not a Space ────────────────────────────────────────────────────────────

    /// <summary>
    /// The Admin partition root resolves to the Admin app. Before this change nothing registered a
    /// node at <c>Admin</c>, so the query below found nothing and <c>/Admin</c> was a typeless
    /// placeholder rendering an empty page.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TheAdminRoot_IsTheAdminApp()
    {
        var nodes = await QueryAs(PlatformAdmin, $"path:{AdminAppNodeType.Path}", TestContext.Current.CancellationToken);

        var admin = nodes.Should().ContainSingle(n => n.Path == AdminAppNodeType.Path).Subject;
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
            .Match(json => json.Contains("global administration") && json.Contains("data sources")
                           && json.Contains("who am i"),
                "a confirmed platform admin sees the Admin app's tabs",
                TestContext.Current.CancellationToken);

        page.Should().Contain("access control", "the Admin partition's own grants are managed here");
        page.Should().NotContain("\"title\":\"metadata\"", "node-management tabs mean nothing on the Admin root");
    }

    /// <summary><c>/Admin</c> with no area opens the app itself — the settings page of the Admin node.</summary>
    [Fact(Timeout = 60000)]
    public async Task TheAdminApp_IsTheDefaultPageOfAdmin()
        => await Render(PlatformAdmin, AdminAppNodeType.Path, new LayoutAreaReference(string.Empty))
            .Should().Within(Budget)
            .Match(json => json.Contains("global administration"),
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
            .Where(json => json.Contains("global administration") || json.Contains("data sources"))
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

        page.Should().NotContain("global administration");
        page.Should().NotContain("data sources");
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

    /// <summary>An old link to an administration tab on the admin's own page redirects into the app.</summary>
    [Fact(Timeout = 60000)]
    public async Task AnOldAdministrationLink_RedirectsIntoTheAdminApp()
        => await Render(PlatformAdmin, PlatformAdmin, Settings(GlobalAdministrationTab.TabId))
            .Should().Within(Budget)
            .Match(json => json.Contains(AdminAppNodeType.TabHref(GlobalAdministrationTab.TabId).ToLowerInvariant()),
                "the relocated tab answers with a redirect into /Admin", TestContext.Current.CancellationToken);
}
