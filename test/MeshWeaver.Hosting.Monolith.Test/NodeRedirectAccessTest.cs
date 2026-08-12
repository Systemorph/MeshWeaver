using System;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>A redirect must not be a way around access control.</b>
///
/// <para>This is the one security property the mechanism has to hold. A <c>Redirect</c> node is
/// discoverable — it sits in a namespace people can browse, and its whole job is to name a path
/// somewhere else. If following one were treated as authorisation to read what it names, anybody
/// who can see the tombstone could read the destination, and a retirement would quietly become a
/// privilege escalation across the 30 external references it was supposed to keep working.</para>
///
/// <para>The design that makes it safe is that a redirect rewrites a PATH and confers nothing: the
/// destination is then resolved, gated and read exactly as if the viewer had typed the destination
/// URL. This test pins that as an A/B on the SAME destination reached the SAME way — <c>carol</c>
/// may read it and must; <c>bob</c> may not and must not. Only the pair proves it, because "bob
/// sees nothing" also passes when redirects are broken outright.</para>
/// </summary>
public class NodeRedirectAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Lobby = "Lobby";                 // both users may read
    private const string Vault = "Vault";                 // only carol may read
    private const string Secret = $"{Vault}/Secret";      // the destination
    private const string SecretPage = $"{Secret}/Page";   // a deep destination
    private const string Tombstone = $"{Lobby}/MovedSecret";  // the redirect bob CAN see

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)      // real row-level security; no PublicAdminAccess
            .AddMeshNodes(
                // 🚨 Only the GRANTS are static. Content nodes are created at runtime, as system,
                // in SeedContent() below: a node seeded through AddMeshNodes is served by the static
                // node provider, which is not row-level-security filtered, so seeding the
                // destination that way would make every read succeed for everyone and this whole
                // test class would assert nothing. The grants must stay static for the opposite
                // reason — SecurityService's synced AccessAssignment query has a debounce window, so
                // a runtime-created assignment races the read in the same test method.
                //
                // bob may read the Lobby — and nothing in the Vault.
                AssignmentNodeFactory.UserRole("bob_viewer_lobby", Role.Viewer.Id, Lobby, accessObject: "bob"),
                // carol may read both — the positive control.
                AssignmentNodeFactory.UserRole("carol_viewer_lobby", Role.Viewer.Id, Lobby, accessObject: "carol"),
                AssignmentNodeFactory.UserRole("carol_viewer_vault", Role.Viewer.Id, Vault, accessObject: "carol"));

    /// <summary>
    /// The content under test, created by the legitimate provisioner (system) so the grants above
    /// are the only thing that varies between the two users. Idempotent-by-construction: each test
    /// gets its own mesh.
    /// </summary>
    private async Task SeedContent()
    {
        await CreateAsSystem(new MeshNode(Lobby) { Name = "Lobby", NodeType = "Group" });
        await CreateAsSystem(new MeshNode(Vault) { Name = "Vault", NodeType = "Group" });
        await CreateAsSystem(MeshNode.FromPath(Secret) with { Name = "Secret", NodeType = "Markdown" });
        await CreateAsSystem(MeshNode.FromPath(SecretPage) with { Name = "Secret Page", NodeType = "Markdown" });
        // The tombstone lives where BOTH users can see it, and points into the Vault.
        await CreateAsSystem(MeshNode.FromPath(Tombstone) with
        {
            Name = "Moved",
            NodeType = NodeRedirectRules.NodeTypeName,
            Content = new NodeRedirect { TargetPath = Secret }
        });
    }

    private Task CreateAsSystem(MeshNode node)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(access.ImpersonateAsSystem, _ => NodeFactory.CreateNode(node))
            .SubscribeOn(TaskPoolScheduler.Default)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
    }

    private IPathResolver Resolver => Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

    private Task<AddressResolution?> ResolveNav(string path) =>
        Resolver.ResolveNavigationPath(path).FirstAsync().Timeout(TimeSpan.FromSeconds(20))
            .ToTask(TestContext.Current.CancellationToken);

    private Task<Permission> Permissions(string path, string userId) =>
        Mesh.GetEffectivePermissions(path, userId).FirstAsync().Timeout(TimeSpan.FromSeconds(20))
            .ToTask(TestContext.Current.CancellationToken);

    /// <summary>
    /// 🚨 <c>SetHostIdentity</c>, not <c>SetCircuitContext</c>. The test host has no Blazor circuit,
    /// and an AsyncLocal written inside an <c>async</c> method is discarded the moment that method
    /// returns — so a circuit-context login silently leaves the base fixture's admin identity in
    /// place and every "denied" assertion below would pass or fail for the wrong reason.
    /// </summary>
    private string _user = string.Empty;

    /// <summary>
    /// Declares who the following reads run as. Re-applied immediately before every gated read
    /// (<see cref="CanSee"/>) rather than once, because seeding runs inside
    /// <c>ImpersonateAsSystem</c> and an awaited continuation can resume on a pool thread whose
    /// captured ExecutionContext still carries <c>system-security</c> — the identity the read gate
    /// consults FIRST (<c>Context ?? CircuitContext</c>) and the one granted <c>Permission.All</c>
    /// unconditionally. Left to drift, every "denied" assertion here would silently measure the
    /// system identity and pass for the wrong reason.
    /// </summary>
    private void Login(string userId)
    {
        _user = userId;
        ApplyIdentity();
    }

    private void ApplyIdentity()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(null);
        access.SetHostIdentity(new AccessContext { ObjectId = _user, Name = _user });
    }

    /// <summary>
    /// Reads a node the way the GUI does — <c>GetMeshNodeStream</c>, whose per-user read gate in
    /// <c>MeshNodeStreamCache</c> evaluates effective permissions for the identity currently on
    /// <c>AccessService</c>. This is the enforcement point a redirect would have to defeat to be a
    /// bypass, which is why the assertion is made here rather than on <c>IMeshService.Query</c>
    /// (whose in-memory monolith path answers as the host and would make this test vacuous).
    /// </summary>
    private async Task<bool> CanSee(string path)
    {
        ApplyIdentity();
        try
        {
            var node = await ReaderHub.GetMeshNodeStream(path)
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(20))
                .ToTask(TestContext.Current.CancellationToken);
            return node is not null;
        }
        catch (Exception ex) when (IsDenial(ex))
        {
            return false;
        }
    }

    private static readonly Address ReaderHubAddress = new("redirect-access-reader", "shared");
    private IMessageHub ReaderHub => Mesh.GetHostedHub(ReaderHubAddress, c => c.AddData());

    /// <summary>
    /// A denial, and ONLY a denial. A timeout or any other fault must keep propagating — a security
    /// test that swallows "something went wrong" as "access was denied" passes for the wrong reason.
    /// </summary>
    private static bool IsDenial(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is UnauthorizedAccessException)
                return true;
            if ((e.Message ?? string.Empty).Contains("lacks Read", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The test that matters: a user denied on the destination is still denied when they arrive
    /// through the redirect, and the permission that decides is the DESTINATION's, evaluated for
    /// THEM.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task A_user_denied_on_the_destination_is_still_denied_when_arriving_via_the_redirect()
    {
        await SeedContent();

        // ── the positive control: carol may read the destination, so the redirect is useful ─────
        Login("carol");
        var carolViaRedirect = await ResolveNav(Tombstone);
        carolViaRedirect!.Prefix.Should().Be(Secret, "the redirect resolves for a user who may follow it");
        (await Permissions(Secret, "carol")).HasFlag(Permission.Read).Should().BeTrue();
        (await CanSee(Secret)).Should().BeTrue(
            "without this half, 'bob sees nothing' below would also pass if redirects were broken outright");

        // ── the property under test: bob is denied on the destination ───────────────────────────
        (await Permissions(Secret, "bob")).HasFlag(Permission.Read).Should().BeFalse(
            "bob has no grant anywhere in the Vault");

        Login("bob");

        // He CAN see the tombstone — it lives in the Lobby, and that is fine: a redirect node names
        // a path, which is no more than any hyperlink does.
        (await CanSee(Tombstone)).Should().BeTrue();

        // Following it rewrites his PATH. It must not rewrite his PERMISSIONS.
        var bobViaRedirect = await ResolveNav(Tombstone);
        bobViaRedirect!.Prefix.Should().Be(Secret,
            "path resolution is not an access decision — it runs under a system bypass for every "
            + "user, exactly as it already does for ordinary paths. The gate is downstream");

        (await Permissions(bobViaRedirect.Prefix, "bob")).HasFlag(Permission.Read).Should().BeFalse(
            "🚨 the permission that decides is the DESTINATION's, evaluated for the ARRIVING user. "
            + "If a redirect could widen this, every retirement would be a privilege escalation");

        (await CanSee(Secret)).Should().BeFalse(
            "🚨 and the actual read is empty for bob — arriving via the redirect gives him exactly "
            + "what typing the destination URL would: nothing");
    }

    /// <summary>
    /// The subtree case, which is the one that matters for a real retirement: a single declaration
    /// covers a whole tree, so if the mechanism leaked it would leak the tree, not one node.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task A_subtree_redirect_does_not_widen_access_to_the_subtree()
    {
        await SeedContent();
        var deep = $"{Tombstone}/Page";

        Login("carol");
        (await ResolveNav(deep))!.Prefix.Should().Be(SecretPage);
        (await CanSee(SecretPage)).Should().BeTrue();

        Login("bob");
        var bobDeep = await ResolveNav(deep);
        bobDeep!.Prefix.Should().Be(SecretPage, "the rewrite is identical for both users");
        (await Permissions(SecretPage, "bob")).HasFlag(Permission.Read).Should().BeFalse();
        (await CanSee(SecretPage)).Should().BeFalse(
            "one declaration must not become one bypass per node in the destination subtree");
    }

    /// <summary>
    /// A redirect whose destination is itself gated: the viewer is not left guessing, and is not
    /// handed the content either. The redirect is followed (the destination is where the content
    /// lives), and the destination's own gate then does what it always does.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task A_redirect_into_gated_content_is_followed_and_then_gated()
    {
        await SeedContent();
        Login("bob");

        var resolved = await ResolveNav(Tombstone);

        resolved!.Prefix.Should().Be(Secret);
        resolved.RedirectedFrom.Should().Be(Tombstone,
            "the viewer is told where they were sent — being denied at a path you did not type, "
            + "with no explanation of how you got there, is the worst of both outcomes");
        (await CanSee(Secret)).Should().BeFalse();
    }
}
