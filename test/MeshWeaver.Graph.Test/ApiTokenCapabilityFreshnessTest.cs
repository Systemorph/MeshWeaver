using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The API-token capability clamp must answer from LIVE data, never from the role snapshot taken
/// when the token was minted.
///
/// <para><b>The defect.</b> <c>ApiToken.Roles</c> is captured at token-creation time, travels on
/// <c>ValidateTokenResponse.Roles</c> and is stamped onto <c>AccessContext.Roles</c> by
/// <c>UserContextMiddleware</c>. <c>PermissionEvaluator</c>'s clamp — which zeroes the WHOLE
/// permission set for a Bearer context that lacks <see cref="Permission.Api"/> — used to consult
/// that snapshot as its escape hatch (<c>ClaimsCarryApi</c>). A snapshot answers a question about
/// NOW with a fact from THEN, and it was wrong in both directions:</para>
/// <list type="bullet">
/// <item><b>Too restrictive.</b> A token minted before its owner held anything Api-bearing could
/// not reach a publicly-readable partition its owner's browser renders fine — and no later grant
/// could fix it, because no later grant rewrites a minted token. Most IdPs emit no role claims at
/// all, so this is the ORDINARY case, not an edge one.</item>
/// <item><b>Too permissive — the security half.</b> The hatch outlived whatever produced it. A
/// token whose mint-time claims carried an Api-bearing role name kept the API surface open
/// forever, over the top of a <c>PartitionAccessPolicy</c> that later declared
/// <c>api: false</c>. Taking API reach away could not take it away from the tokens that already
/// existed.</item>
/// </list>
///
/// <para><b>The fix.</b> The capability is derived from THIS path's own live public grant and
/// policy cap (<c>PermissionEvaluator.PublicSurfaceCarriesApi</c>) — both already in the fold, so
/// there is no extra query, no cross-schema fan-out and no re-entry into the evaluator. Nothing in
/// the evaluator reads <c>AccessContext.Roles</c> any more.</para>
///
/// <para>🚨 Every case below is written so it CAN fail, and that was measured rather than assumed.
/// Restoring the claim hatch reds exactly <see cref="StaleClaimRoles_CannotDefeat"/>,
/// <see cref="TokenWithNoClaims_ReadsAPubliclyReadablePartition"/> and
/// <see cref="ClaimRoles_ChangeNoVerdict"/> while the "must still work" cases stay green; deleting
/// the clamp outright reds <see cref="StaleClaimRoles_CannotDefeat"/>, so the clamp is still
/// load-bearing and this change did not merely neuter it.</para>
/// </summary>
public class ApiTokenCapabilityFreshnessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The person the token belongs to. Deliberately NOT the DevLogin harness admin.</summary>
    private const string TokenUser = "token-holder";

    /// <summary>Publicly readable by policy — the shape <c>PackageInstaller</c> writes for
    /// <c>Doc/</c>, <c>Agent/</c> and every installed package partition.</summary>
    private const string PublicPartition = "PublicCatalog";

    /// <summary>Publicly readable in a browser, explicitly NOT reachable through the API.</summary>
    private const string ApiCappedPartition = "CappedCatalog";

    /// <summary>No public surface; the token holder has a real Editor grant here.</summary>
    private const string GrantedPartition = "GrantedSpace";

    /// <summary>No public surface and no grant — the control arm.</summary>
    private const string ForeignPartition = "ForeignSpace";

    /// <summary>Starts with no grant; one is WRITTEN AT RUNTIME, long after the token existed.
    /// Its own partition so it cannot perturb the control arm above even if this suite ever opts
    /// into a shared mesh.</summary>
    private const string LateGrantPartition = "LateGrantSpace";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter chains PublicAdminAccess(), which
    // grants Public the Admin role in every default partition — under it every identity carries
    // Admin (hence Api) everywhere and every assertion here would pass vacuously.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(PublicPartition) { Name = "Public Catalog", NodeType = "Markdown" },
                new MeshNode("Page", PublicPartition) { Name = "Public Page", NodeType = "Markdown" },
                AssignmentNodeFactory.Policy(PublicPartition,
                    new PartitionAccessPolicy { PublicRead = true }),

                new MeshNode(ApiCappedPartition) { Name = "Capped Catalog", NodeType = "Markdown" },
                new MeshNode("Page", ApiCappedPartition) { Name = "Capped Page", NodeType = "Markdown" },
                // "Readable by anyone in a browser; not reachable through the API." The Api cap is
                // the administrator's revocation of API reach — the thing a stale token defeated.
                AssignmentNodeFactory.Policy(ApiCappedPartition,
                    new PartitionAccessPolicy { PublicRead = true, Api = false }),

                new MeshNode(GrantedPartition) { Name = "Granted Space", NodeType = "Markdown" },
                new MeshNode("Page", GrantedPartition) { Name = "Granted Page", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(TokenUser, "Editor", GrantedPartition),

                new MeshNode(ForeignPartition) { Name = "Foreign Space", NodeType = "Markdown" },
                new MeshNode("Page", ForeignPartition) { Name = "Foreign Page", NodeType = "Markdown" },

                new MeshNode(LateGrantPartition) { Name = "Late Grant Space", NodeType = "Markdown" },
                new MeshNode("Page", LateGrantPartition) { Name = "Late Grant Page", NodeType = "Markdown" });

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>
    /// A Bearer request as the middleware builds it. <paramref name="mintTimeRoles"/> is the
    /// snapshot from <c>ApiToken.Roles</c>; the whole point of this suite is that it must not
    /// change any answer.
    ///
    /// <para>🚨 BOTH contexts, deliberately: the evaluator snapshots
    /// <c>Context ?? CircuitContext</c> on the caller's thread, so a circuit-only switch can be
    /// shadowed by whatever last wrote <c>Context</c> and leave the test asserting as the wrong
    /// principal.</para>
    /// </summary>
    private void BecomeToken(params string[] mintTimeRoles)
    {
        var ctx = new AccessContext
        {
            ObjectId = TokenUser,
            Name = "Token Holder",
            Roles = mintTimeRoles,
            IsApiToken = true,
        };
        Access.SetContext(ctx);
        Access.SetHostIdentity(ctx);
    }

    /// <summary>The same person in a browser — the surface the issue reports as "works fine".</summary>
    private void BecomeBrowser()
    {
        var ctx = new AccessContext { ObjectId = TokenUser, Name = "Token Holder" };
        Access.SetContext(ctx);
        Access.SetHostIdentity(ctx);
    }

    private Task<Permission> Effective(string path) =>
        Mesh.GetEffectivePermissions(path, TokenUser)
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// 🚨 THE SECURITY PIN. The administrator has said "this partition is not reachable through
    /// the API" (<c>api: false</c>). A token minted while its owner's claims still carried an
    /// Api-bearing role must NOT be able to spend that stale fact against the live policy.
    ///
    /// <para>Pre-fix this returned <c>Read</c>: the public grant is ORed in after the cap, so
    /// <c>p</c> was <c>Read</c> without <c>Api</c>, and <c>ClaimsCarryApi(["Admin"])</c> answered
    /// true and waved it through. The stale claim outranked the live policy, permanently and for
    /// every token minted before the policy was written.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task StaleClaimRoles_CannotDefeat()
    {
        BecomeToken("Admin", "Editor");

        (await Effective($"{ApiCappedPartition}/Page"))
            .Should().Be(Permission.None,
                "a policy that caps Api out means 'not reachable through the API' — a role " +
                "snapshot taken when the token was minted must not outrank it");
    }

    /// <summary>
    /// The companion measurement that makes the pin above meaningful rather than merely strict:
    /// the SAME node, the SAME person, in a browser. The cap removes the Api bit and nothing else,
    /// so the page stays readable — which is exactly what <c>PublicRead</c> means and why the
    /// denial above is a capability decision, not a data one.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ApiCappedPartition_IsStillReadableInABrowser()
    {
        BecomeBrowser();

        (await Effective($"{ApiCappedPartition}/Page"))
            .HasFlag(Permission.Read).Should().BeTrue(
                "capping Api withdraws the API surface, not the public page");
    }

    /// <summary>
    /// 🚨 THE ISSUE'S DIRECTION. A token carrying NO claims at all — what every token minted
    /// through an IdP that emits no role claims looks like — reads a publicly-readable partition.
    ///
    /// <para>Pre-fix this returned <c>Permission.None</c>: <c>p</c> was <c>Read</c> from the
    /// public grant with no <c>Api</c> bit, the claims were empty, and the clamp zeroed the whole
    /// set. The user's browser rendered the same page, and re-minting the token changed nothing
    /// because the claim list was empty at every mint.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TokenWithNoClaims_ReadsAPubliclyReadablePartition()
    {
        BecomeToken();

        (await Effective($"{PublicPartition}/Page"))
            .HasFlag(Permission.Read).Should().BeTrue(
                "a page every anonymous browser may read is not secret from an API client — and " +
                "no re-mint could have produced this, because the claim list is empty at every mint");
    }

    /// <summary>
    /// The clamp is still a clamp. No grant, no public surface, and a mint-time claim list that
    /// says "Admin": still nothing. Claim roles never were a data grant (the 2026-08-05 paywall
    /// fix) and removing their last foothold must not have quietly restored one.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task StaleClaimRoles_GrantNothingWhereThereIsNoLiveAuthority()
    {
        BecomeToken("Admin");

        (await Effective($"{ForeignPartition}/Page"))
            .Should().Be(Permission.None,
                "a platform role claim is not cross-partition data access, snapshot or not");
    }

    /// <summary>
    /// The regression the mint-time stamp was introduced to prevent, pinned so it cannot come
    /// back: a claimless token with a REAL <c>Editor</c> grant on the target scope reads it.
    /// <c>Role.Editor</c> carries <see cref="Permission.Api"/>, so the clamp never fires — which
    /// is why the "0 roles → 0 perms → DENY" story the old comments told stopped being true long
    /// before this change.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TokenWithNoClaims_ReadsWhatItsOwnGrantAllows()
    {
        BecomeToken();

        var perms = await Effective($"{GrantedPartition}/Page");
        perms.HasFlag(Permission.Read).Should().BeTrue(
            "the grant is read live off the target path — a token never needs re-minting to see it");
        perms.HasFlag(Permission.Api).Should().BeTrue(
            "Editor carries Api, so the capability comes from the grant itself");
    }

    /// <summary>
    /// 🚨 THE ISSUE'S CASE, END TO END AND IN TIME ORDER: the token exists FIRST, the grant is
    /// written AFTERWARDS, and the same token — never re-minted, never re-validated — sees it.
    ///
    /// <para>The negative half is asserted first and is what makes it a measurement rather than an
    /// assumption: the very same read is refused before the grant lands. Nothing about the token
    /// changes between the two reads; only the mesh does.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AGrantWrittenAfterTheTokenExists_IsSeenWithoutReminting()
    {
        BecomeToken();

        // Before: no grant anywhere on this path, no public surface.
        (await Effective($"{LateGrantPartition}/Page"))
            .Should().Be(Permission.None, "the control arm — this token is refused here");

        // The grant lands, written by an administrator long after the token was minted.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var assignment = AssignmentNodeFactory.UserRole(TokenUser, "Editor", LateGrantPartition);
        await Observable.Create<MeshNode>(observer =>
            {
                // As System: writing an AccessAssignment is an administrative act, and the point
                // under test is what the TOKEN sees afterwards, not who may write the grant.
                using (Access.ImpersonateAsSystem())
                    return meshService.CreateNode(assignment).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

        // Re-assert the token identity: ImpersonateAsSystem above is scoped, but being explicit
        // means a leaked System scope cannot make this pass while proving nothing.
        BecomeToken();

        // After: the SAME token, unchanged, on the SAME path. The fold re-emits when the grant
        // lands, so this is a wait on the condition — never a sleep.
        await Mesh.GetEffectivePermissions($"{LateGrantPartition}/Page", TokenUser)
            .Where(p => p.HasFlag(Permission.Read))
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The structural anti-regression: on the two paths where the old evaluator DID diverge on
    /// claims, the verdict is now identical with and without them. <c>AccessContext.Roles</c> is
    /// carried for diagnostics and for <c>AccessControlPipeline</c>'s context-restore cue — a
    /// future "just check the claims" is a regression, not a shortcut.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ClaimRoles_ChangeNoVerdict()
    {
        BecomeToken();
        var cappedWithout = await Effective($"{ApiCappedPartition}/Page");
        var publicWithout = await Effective($"{PublicPartition}/Page");

        BecomeToken("Admin", "Editor", "Viewer");
        var cappedWith = await Effective($"{ApiCappedPartition}/Page");
        var publicWith = await Effective($"{PublicPartition}/Page");

        cappedWith.Should().Be(cappedWithout,
            "the Api cap is decided by the live policy, not by what the token remembers");
        publicWith.Should().Be(publicWithout,
            "the public surface is decided by the live policy, not by what the token remembers");
    }
}
