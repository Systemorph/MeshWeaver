using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// 🚨 <b>The control behind the seeding rule in Doc/Architecture/AccessControl → "The convergence
/// contract" (issue #2742): a leg of the permission fold may be <c>StartWith</c>-seeded EMPTY only
/// if its contribution is PURELY ADDITIVE — and only <c>ObserveGatedNodes</c> is.</b>
///
/// <para><b>Why the rule needs a control.</b> Seeding a starving leg is the obvious cure for the
/// fold's liveness problem, it is already applied to <c>ObserveGatedNodes</c>, and the reasoning that
/// justifies it there ("the empty seed starts STRICTER") reads as though it generalises. It does not.
/// <c>ObserveGatedNodes</c> is the ONE leg that can only ADD a permission — <c>GateGrant</c> never
/// subtracts, and says so. Every other leg carries SUBTRACTION as well as addition: denied role
/// assignments (<c>ComputeScopeRoles</c> derives <c>Denied</c> from the very same nodes as
/// <c>Granted</c>), permission caps and <c>BreaksInheritance</c> (<c>PartitionAccessPolicy</c>), and
/// group membership — whose subject set decides which DENIALS match every bit as much as which
/// grants do. Seed one of those empty and its subtractions vanish from the fold's first emission,
/// which is then MORE permissive than the truth.</para>
///
/// <para><b>Why the FIRST emission is the one under test.</b> The gate takes exactly one:
/// <c>AccessControlPipeline</c> runs <c>CheckPermissionOutcome(…).TakeDecisionOutsideGate()</c>, and
/// <c>TakeDecisionOutsideGate</c> is a <c>Take(1)</c>. The fold's first emission IS the verdict for
/// every <c>[RequiresPermission]</c> delivery — a "temporary" pre-load window is not temporary from
/// the gate's point of view, it is the answer.</para>
///
/// <para><b>🚨 Why the fold must be COLD, and how that is guaranteed.</b> On a WARM fold a seed is
/// invisible: the <c>$security-*</c> queries are process-wide <c>Replay(1)</c> streams, so a
/// <c>StartWith(empty)</c> emits its seed and the replayed value back-to-back during Subscribe, and
/// <c>CombineLatest</c> — which cannot emit until its LAST source has produced — never sees the seed
/// as the latest value. A guard that warms the scope first therefore passes with the seed in place,
/// having proved nothing (measured: all three cases green against a seeded fold). So every case here
/// seeds its data under <c>ImpersonateAsSystem</c>, which short-circuits
/// <c>GetEffectivePermissions</c> before any query is built (<c>WellKnownUsers.System</c> returns
/// immediately), leaving that scope's queries UNSUBSCRIBED. The assertion is then the first emission
/// of a genuinely cold fold — where a seed does land as the answer.</para>
///
/// <para><b>What each case actually detects — measured, not assumed.</b> A seed only changes the
/// answer while its leg is the LAST to produce, so how directly a case fires depends on which leg is
/// slowest. Applying <c>.StartWith(empty)</c> and re-running gave:</para>
/// <list type="bullet">
///   <item><description><b>assignment leg</b> — RED on an assignment-only seed, and RED on an
///     all-legs seed. It is a true seed DETECTOR: that leg carries the rows, so it is reliably the
///     last to answer on a cold scope.</description></item>
///   <item><description><b>policy and membership legs</b> — green under a policy-only or
///     membership-only seed, because the assignment leg answers later and its real value is in place
///     by the time the fold first emits. These two are therefore SEMANTIC pins, not seed detectors:
///     they pin the fact the rule rests on — that these legs SUBTRACT — which is what makes an empty
///     seed on either of them fail-OPEN whenever they ARE the slow leg (a cold global membership
///     query, an evicted policy query). Catching that case directly needs fault injection into
///     <c>IMeshNodeStreamCache.GetQuery</c>, which this suite does not have.</description></item>
/// </list>
/// </summary>
public class PermissionFoldLegSeedGuardTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddRowLevelSecurity();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private AccessService AccessService => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>A GroupMembership node placing <paramref name="member"/> into <paramref name="groupPath"/>.</summary>
    private static MeshNode Membership(string member, string groupPath) =>
        new($"{member.Replace('/', '_')}_Membership", groupPath)
        {
            NodeType = "GroupMembership",
            Name = $"{member} membership",
            MainNode = $"{groupPath}/{member.Replace('/', '_')}_Membership",
            Content = new GroupMembership
            {
                Member = member,
                Groups = [new MembershipEntry { Group = groupPath }]
            }
        };

    /// <summary>
    /// Writes the fixture's access nodes as SYSTEM. Two jobs: the writes are unconditionally
    /// authorised, and — the load-bearing one — the System short-circuit means no permission fold
    /// runs for these scopes, so their <c>$security-*</c> queries stay COLD for the assertion.
    /// </summary>
    private async Task SeedAsSystem(params MeshNode[] nodes)
    {
        using (AccessService.ImpersonateAsSystem())
            foreach (var node in nodes)
                await MeshService.CreateNode(node).Should().Emit();
    }

    /// <summary>
    /// The first emission of a COLD fold — the value the <c>[RequiresPermission]</c> gate takes as
    /// its verdict.
    /// </summary>
    /// <remarks>
    /// <c>Should().Emit()</c> — the repo's reactive assertion — rather than a <c>.ToTask()</c>
    /// bridge (ObservableToTaskBridgeGuard). It takes the FIRST emission, which is precisely the
    /// value under test, and FAILS if the stream completes without one, so a fold that produced
    /// nothing can never be mistaken here for a fold that answered <c>Permission.None</c>.
    /// </remarks>
    private Task<Permission> FirstVerdict(string path, string userId)
        => Mesh.GetEffectivePermissions(path, userId).Should()
            .Emit("the gate takes the fold's FIRST emission as its verdict");

    [Fact(Timeout = 30_000)]
    public async Task TheMembershipLegIsSUBTRACTIVE_SoAnEmptySeedWouldGrantWhatAGroupDenyRevokes()
    {
        await SeedAsSystem(
            // seeduser holds Viewer DIRECTLY at SeedGroup …
            AssignmentNodeFactory.UserRole("seeduser", "Viewer", "SeedGroup"),
            // … and belongs to a cohort DENIED Viewer at the same scope. The deny matches ONLY
            // because the cohort is in the viewer's SUBJECT set — i.e. it is the membership leg
            // that makes it apply at all.
            AssignmentNodeFactory.UserRole("SeedGroup/Cohort", "Viewer", "SeedGroup", denied: true),
            Membership("seeduser", "SeedGroup/Cohort"));

        // 🚨 THE SUBTRACTIVITY PIN (see the class doc for what this does and does not detect).
        // The deny reaches this viewer ONLY through the membership leg. With that leg seeded empty and
        // slow, the first
        // emission resolves subjects = { seeduser } only: the direct Viewer grant survives, the
        // cohort's deny does not, and the gate's Take(1) reads Read on a node the viewer must not
        // see. FAIL-OPEN.
        var denied = await FirstVerdict("SeedGroup/Doc", "seeduser");
        denied.Should().Be(Permission.None,
            "the membership leg decides which DENIALS match, not only which grants — so seeding it "
            + "empty drops a group deny and the fold's first emission grants what the truth revokes");

        // POSITIVE CONTROL. "Permission.None" is also what a fixture that never landed would produce,
        // so prove the grant half of the same fixture really works: a direct grant at the SAME scope,
        // for a user in no group, reads. Taken second on purpose — it warms the scope.
        await SeedAsSystem(AssignmentNodeFactory.UserRole("seedcontrol", "Viewer", "SeedGroup"));
        await Mesh.GetEffectivePermissions("SeedGroup/Doc", "seedcontrol")
            .Should().Match(p => p.HasFlag(Permission.Read));
    }

    [Fact(Timeout = 30_000)]
    public async Task ThePolicyLegIsSUBTRACTIVE_SoAnEmptySeedWouldIgnoreARuntimePermissionCap()
    {
        await SeedAsSystem(
            // An Editor at SeedPolicy — the Editor role carries Update …
            AssignmentNodeFactory.UserRole("seedpolicyuser", "Editor", "SeedPolicy"),
            // … under a RUNTIME policy that caps Update away for the scope and everything below it.
            AssignmentNodeFactory.Policy("SeedPolicy", new PartitionAccessPolicy { Update = false }));

        var verdict = await FirstVerdict("SeedPolicy/Doc", "seedpolicyuser");

        // 🚨 THE SUBTRACTIVITY PIN (see the class doc). With ObserveScopePolicies seeded empty and
        // slow, ComputeRoleState falls back to the
        // STATIC policy map, which has no entry for this scope — so the cap is (Permission)~0 and the
        // Editor keeps Update. An absent policy is PERMISSIVE by construction, which is exactly why
        // this leg can never carry an empty seed. FAIL-OPEN.
        verdict.HasFlag(Permission.Update).Should().BeFalse(
            "a policy RESTRICTS — its absence widens, so an empty seed on the policy leg hands the "
            + "gate a first emission that ignores every runtime cap and BreaksInheritance boundary");

        // POSITIVE CONTROL, in the same emission: the grant itself did land, so the assertion above
        // is about the cap and not about an empty fixture.
        verdict.HasFlag(Permission.Read).Should().BeTrue(
            "the Editor grant is real — only Update is capped");
    }

    [Fact(Timeout = 30_000)]
    public async Task TheAssignmentLegCarriesEVERYRuntimeGrant_SoAnEmptySeedWouldDenyEntitledUsers()
    {
        // The ordinary case: a grant that exists only at runtime — no IStaticNodeProvider declares
        // it — which is what almost every real grant is.
        await SeedAsSystem(AssignmentNodeFactory.UserRole("seedassignuser", "Viewer", "SeedAssign"));

        // 🚨 THE SEED DETECTOR — measured RED under an assignment-leg seed, in isolation and in
        // combination. Seeding ObserveEffectiveAssignments empty is
        // fail-closed in the permission lattice and still WRONG: the gate takes the first emission,
        // so an entitled user reading a cold scope is answered Permission.None and the delivery comes
        // back "Access denied" — the false, actionable-looking verdict issue #974 exists to prevent,
        // now fired on every cold scope. (It is ALSO fail-open in a narrower way: an empty seed drops
        // runtime DENIALS of roles that survive it — static grants and the self-partition Admin.)
        var verdict = await FirstVerdict("SeedAssign/Doc", "seedassignuser");
        verdict.HasFlag(Permission.Read).Should().BeTrue(
            "the assignment leg carries the grant itself — seeding it empty turns a cold-scope read "
            + "from a wait into a spurious denial, which is a worse answer than the wait");
    }
}
