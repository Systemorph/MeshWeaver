using System.Reactive.Linq;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 SIGNING IN MUST NOT DEPEND ON A MESH-WIDE QUERY. Issue #3202.
///
/// <para><b>What production showed.</b> Every page on a portal built from MeshWeaver.Plugins main at
/// or after <c>fe20fe2a</c> answered 503 to every signed-in user — <i>"We could not check your
/// account just now"</i> — with the log naming the cause:
/// <c>LoadUserRoles(…) faulted: UnanchoredQueryException</c>. Plugins #1231 made fan-out opt-in
/// (a query that names no partition and does not ask to span them is REFUSED), and the sign-in
/// role read was exactly that shape:
/// <c>nodeType:AccessAssignment content.accessObject:"{user}" scope:subtree</c>. The middleware
/// behaved to the #637 contract — an infrastructure fault is never presented as "you have no
/// account" — so the refusal became a 503 on every request instead of a silent privilege strip.
/// Both production portals were frozen on older images until this landed.</para>
///
/// <para><b>The fix is three ANCHORED reads, not <c>partitions:all</c>.</b> A user's PLATFORM roles
/// are granted in exactly three places by contract (<c>Doc/Architecture/AccessControl</c> → "Where
/// to look"): the root scope (<c>_Access</c>, the registered global satellite — ONE schema), the
/// <c>Admin</c> partition (the one meaning of "global admin"), and the user's own partition. Each
/// pins one schema; the union is the complete answer, and <c>AccessContext.Roles</c> is read by no
/// permission decision (see <see cref="OnboardingMiddleware.RoleQueries"/>). Declaring the fan-out
/// instead would have restored the 199-schema UNION on every request that #2194 measured at ~4 s.</para>
///
/// <para>Two tests here, and both are needed: the GUARD classifies every query the middleware
/// issues with the planner's own decision (so a future edit that re-introduces an unanchored read
/// fails here, in this repo, instead of at runtime in another), and the BEHAVIOURAL test proves
/// the three legs return the grants they are meant to — a guard alone could pass over three
/// anchored reads that read the wrong homes.</para>
/// </summary>
public class SignInReadsAreAnchoredTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The user with a grant in every home the fold reads, and one it must not.</summary>
    private const string GrantedUser = "sign-in-granted";

    /// <summary>A user with no grants anywhere: the fold RESOLVES to empty, it does not fault.</summary>
    private const string UngrantedUser = "sign-in-ungranted";

    /// <summary>A partition that is none of the three homes — a data grant, not a platform role.</summary>
    private const string Elsewhere = "SignInElsewhere";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // ConfigureMeshBase, not base.ConfigureMesh: PublicAdminAccess would hand Public the Admin
    // role in every default partition and blur what the three legs return.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(GrantedUser) { Name = "Granted", NodeType = "User" },
                new MeshNode(Elsewhere) { Name = "Elsewhere", NodeType = "Markdown" },
                // Root scope: _Access/{user}_Access — the registered global satellite.
                AssignmentNodeFactory.UserRole(GrantedUser, "Viewer"),
                // The Admin partition: Admin/_Access/{user}_Access — the platform-admin grant.
                AssignmentNodeFactory.UserRole(GrantedUser, "Admin", OnboardingMiddleware.AdminPartition),
                // The user's own partition: {user}/_Access/{user}_Access — Admin of my own home.
                AssignmentNodeFactory.UserRole(GrantedUser, "Editor", GrantedUser),
                // A grant in an arbitrary space: a DATA permission the fold evaluates from that
                // space's own _Access at check time, not a platform role.
                AssignmentNodeFactory.UserRole(GrantedUser, "Owner", Elsewhere));

    // ————————————————————————— the guard

    /// <summary>
    /// Every query the middleware issues is served by the planner — anchored to one partition,
    /// or pinned by a registered routing rule. The decision is the planner's own
    /// (<c>IsSufficientlySpecified || ResolvesByRoutingHint</c>), taken against the REAL routing
    /// rules of this mesh, so a rule that stopped resolving would fail here too.
    /// </summary>
    [Fact]
    public void EveryQueryTheMiddlewareIssuesIsServedByThePlanner()
    {
        var configuration = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();

        foreach (var leg in OnboardingMiddleware.RoleQueries(GrantedUser))
        {
            QueryRouteClassifier.VerdictOf(leg, configuration).Should().Be(PlannerVerdict.Anchored,
                $"a sign-in role leg names its partition — '{leg}'");
            QueryRouteClassifier.RouteOf(leg).Kind.Should().Be(QueryRoute.Pinned,
                $"and the router serves it from ONE schema — '{leg}'");
        }

        // The account lookup names no partition ON PURPOSE and is served through UserNodeType's
        // routing rule (nodeType:User → Auth). The planner consults that rule before refusing, so
        // this is the verdict it reaches — and the rule must resolve this EXACT text.
        var userQuery = OnboardingMiddleware.UserByEmailQuery("someone@example.com");
        QueryRouteClassifier.VerdictOf(userQuery, configuration).Should().Be(PlannerVerdict.PinnedByRoutingRule,
            "nodeType:User with no path is routed to the Auth partition by UserNodeType's rule");
        configuration.ResolveRoutingHints(new QueryParser().Parse(userQuery)).Partition
            .Should().Be("Auth", "the auth-mirror partition holds every User node in the mesh");
    }

    /// <summary>
    /// The controls that make the guard non-vacuous: the shape the sign-in read HAD is refused by
    /// the same decision, and refused for the reason the issue names — no rule pins
    /// <c>AccessAssignment</c> (it is one of the fold's never-narrowed types).
    /// </summary>
    [Fact]
    public void ThePreviousSignInReadWouldBeRefused()
    {
        var configuration = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();
        const string before = "nodeType:AccessAssignment content.accessObject:\"sign-in-granted\" scope:subtree limit:all";

        QueryRouteClassifier.VerdictOf(before, configuration).Should().Be(PlannerVerdict.Refused,
            "the read #3202 replaced names no partition, declares no fan-out, and no routing rule pins it");
        // …and the same decision with NO rules at all refuses the user lookup too — the guard can
        // fail, and it fails on the half that depends on the rule being registered.
        QueryRouteClassifier.VerdictOf(OnboardingMiddleware.UserByEmailQuery("a@b.c"), configuration: null)
            .Should().Be(PlannerVerdict.Refused, "without UserNodeType's rule nodeType:User is unanchored");
    }

    // ————————————————————————— the behaviour

    /// <summary>
    /// The fold returns the grant from EACH of the three homes — root, Admin, own — and only those:
    /// a grant in an arbitrary space is a data permission, not a platform role.
    /// </summary>
    [Fact]
    public async Task TheRoleFoldReadsAllThreePlatformHomes()
    {
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(SignInReadsAreAnchoredTest));

        var outcome = await OnboardingMiddleware.LoadUserRoles(Mesh.GetWorkspace(), GrantedUser, logger, Budget)
            .Take(1).Timeout(Budget).Await(TestContext.Current.CancellationToken);

        outcome.IsUnavailable.Should().BeFalse($"the read must RESOLVE — {outcome.UnavailableReason}");
        outcome.Value.Should().NotBeNull();
        outcome.Value!.Should().Contain("Viewer", "the ROOT grant (_Access) is read from the registered global satellite");
        outcome.Value.Should().Contain("Admin", "the Admin/_Access grant IS the platform-admin role");
        outcome.Value.Should().Contain("Editor", "the user's own partition holds the Admin-of-my-home grant");
        outcome.Value.Should().NotContain("Owner",
            "a grant in an arbitrary space is a DATA permission the fold evaluates at check time from "
            + "that space's own _Access — folding it in was incidental to the mesh-wide query, and "
            + "reading it would mean reading every partition again");
    }

    /// <summary>
    /// No grants anywhere is a DEFINITIVE empty set — <c>Resolved([])</c>, never
    /// <c>Unavailable</c>. The #637 contract: an empty role set means exactly one thing, "the read
    /// completed and found no grants"; a 503 is reserved for a read that reached no verdict.
    /// </summary>
    [Fact]
    public async Task AUserWithNoGrantsResolvesToNoRoles_NotToUnavailable()
    {
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(SignInReadsAreAnchoredTest));

        var outcome = await OnboardingMiddleware.LoadUserRoles(Mesh.GetWorkspace(), UngrantedUser, logger, Budget)
            .Take(1).Timeout(Budget).Await(TestContext.Current.CancellationToken);

        outcome.IsUnavailable.Should().BeFalse(
            $"three anchored reads over partitions holding nothing for this user are an ANSWER — {outcome.UnavailableReason}");
        outcome.Value.Should().NotBeNull();
        outcome.Value!.Should().BeEmpty();
    }
}
