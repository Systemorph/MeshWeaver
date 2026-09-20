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
/// 🚨 <b>In THIS fold, a Public/Anonymous DENY does not suppress an inherited
/// <c>PartitionAccessPolicy.PublicRead</c> — and the SQL fold's own comment says it does.</b> The C#
/// half is measured here; the divergence that follows from it is a CLAIM about a path this file does
/// not execute (Systemorph/MeshWeaver#4716).
///
/// <para><b>What this file pins, and what it does not.</b> Every assertion below is a MEASUREMENT of
/// the C# <c>PermissionEvaluator</c> against a real monolith mesh. The Postgres half is NOT executed
/// here; what is known about it is its projection's own comment in
/// <c>MeshWeaver.Hosting.PostgreSql/PostgreSqlSchemaInitializer.cs</c> (MeshWeaver.Plugins), which
/// projects a <c>PublicRead</c> policy as allow-<c>Read</c> rows at the policy's prefix and then
/// states: <i>"A deny at a LONGER prefix still wins the per-subject longest-prefix query fold; that
/// is the store-gating shape and it is intentional."</i> So the intended behaviour is the OPPOSITE of
/// what this file measures, and the divergence is the defect — not the behaviour recorded here.
/// 🚨 <b>So do not "fix" a red in this file by relaxing an assertion.</b> When the folds are
/// reconciled, the first theory below flips to <c>BeFalse</c> and this summary is what says why.</para>
///
/// <para><b>Why it matters: the two shapes of a read path disagreeing IS the paywall bypass.</b> The
/// evaluator carries its own account of the last time (2026-08-05): a <c>get</c> by exact path served
/// 79,650 characters of paid course content to an unentitled caller while <c>search</c>, over the SQL
/// fold, correctly denied the same node. A Public/Anonymous deny written under a <c>PublicRead</c>
/// partition reproduces that shape exactly — hidden from every listing, readable by exact path.</para>
///
/// <para><b>Three components assert the behaviour this file falsifies for the C# fold.</b>
/// <c>PackageInstaller.EnsurePartitionPublicRead</c>'s remarks stated it as fact; #4716's triage cited
/// those remarks to conclude a per-path deny on a submission inbox <i>"is not hypothetical"</i>; and
/// the Store's <c>PluginGate</c> pre-installed arm (in-mesh source in MeshWeaver.Plugins, invisible to
/// any build or grep over this repository) IMPLEMENTS it — for every segment a manifest declares in
/// <c>ProtectedSegments</c> it writes exactly this deny pair, under a comment saying that without it
/// <i>"an open partition would publish a satellite holding user-submitted data (Feedback's
/// _Submissions inbox)"</i>.</para>
///
/// <para><b>Where the divergence lives, in one line.</b> <c>ComputeRoleState</c> subtracts denied roles
/// from <c>roleIds</c>, and ORs the public grant in SEPARATELY and afterwards
/// (<c>publicGrant |= Permission.Read</c>), accumulating it down the chain. A deny removes a ROLE, and
/// <c>PublicRead</c> is not a role, so there is nothing for it to take away. SQL instead resolves one
/// row set by LONGEST PREFIX, where a deeper deny row simply wins. Closing the gap means folding the
/// public chain last-writer-wins over the well-known subjects' rows, which this fold cannot do today:
/// <c>ComputeScopeRoles</c> filters assignments to the EVALUATED subject, so the Public/Anonymous deny
/// at a deeper scope is not even in the state <c>ComputeRoleState</c> receives.</para>
///
/// <para>Full measurement, and what each candidate remedy costs:
/// <c>Doc/Architecture/PublicReadAndDenies</c>.</para>
/// </summary>
public class PublicReadIsNotSuppressedByADenyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A real signed-in subject holding Viewer at a root — the "reviewer" of the third theory.</summary>
    private const string Reviewer = "deny-vs-publicread-reviewer";

    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin,
    // which would make every reading below Read regardless of the model.
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            // ── Shape 1: public read from the POLICY, plus a child deny ──────────────────
            new MeshNode("PolicyOpen") { NodeType = "Markdown" },
            AssignmentNodeFactory.Policy("PolicyOpen", new PartitionAccessPolicy { PublicRead = true }),
            new MeshNode("Cover", "PolicyOpen") { NodeType = "Markdown" },
            new MeshNode("Gated", "PolicyOpen") { NodeType = "Markdown" },
            AssignmentNodeFactory.UserRole(WellKnownUsers.Public, "Viewer", "PolicyOpen/Gated", denied: true),
            AssignmentNodeFactory.UserRole(WellKnownUsers.Anonymous, "Viewer", "PolicyOpen/Gated", denied: true),
            new MeshNode("Page", "PolicyOpen/Gated") { NodeType = "Markdown" },

            // ── Shape 2: public read from root GRANTS, plus the same child deny ──────────
            new MeshNode("GrantOpen") { NodeType = "Markdown" },
            AssignmentNodeFactory.UserRole(WellKnownUsers.Public, "Viewer", "GrantOpen"),
            AssignmentNodeFactory.UserRole(WellKnownUsers.Anonymous, "Viewer", "GrantOpen"),
            new MeshNode("Cover", "GrantOpen") { NodeType = "Markdown" },
            new MeshNode("Gated", "GrantOpen") { NodeType = "Markdown" },
            AssignmentNodeFactory.UserRole(WellKnownUsers.Public, "Viewer", "GrantOpen/Gated", denied: true),
            AssignmentNodeFactory.UserRole(WellKnownUsers.Anonymous, "Viewer", "GrantOpen/Gated", denied: true),
            new MeshNode("Page", "GrantOpen/Gated") { NodeType = "Markdown" },

            // ── Shape 3: the only thing that suppresses the policy grant — and what it costs ──
            new MeshNode("CapOpen") { NodeType = "Markdown" },
            AssignmentNodeFactory.Policy("CapOpen", new PartitionAccessPolicy { PublicRead = true }),
            AssignmentNodeFactory.UserRole(Reviewer, "Viewer", "CapOpen"),
            new MeshNode("Gated", "CapOpen") { NodeType = "Markdown" },
            AssignmentNodeFactory.Policy("CapOpen/Gated", new PartitionAccessPolicy { Read = false }),
            new MeshNode("Page", "CapOpen/Gated") { NodeType = "Markdown" });

    /// <summary>
    /// The effective permission a SUBJECT holds on a path. Asserts the FIRST decision, so an initial
    /// leak cannot hide behind a later matching answer.
    /// </summary>
    private Task<Permission> Effective(string path, string subject, CancellationToken cancellationToken)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var context = new AccessContext { ObjectId = subject, Name = subject };
        access.SetContext(context);
        access.SetHostIdentity(context);
        return Mesh.GetEffectivePermissions(path, subject)
            .FirstAsync().Timeout(TestTimeouts.Quick).Await(cancellationToken);
    }

    private async Task<bool> CanRead(string path, string subject, CancellationToken cancellationToken)
    {
        var permission = await Effective(path, subject, cancellationToken);
        Output.WriteLine($"{subject} on {path}: {permission}");
        return permission.HasFlag(Permission.Read);
    }

    /// <summary>
    /// 🚨 <b>THE FALSIFICATION.</b> Both well-known subjects still READ a child carrying their own
    /// Viewer DENY, because the partition's policy grants public read and the fold ORs that in after
    /// the deny subtraction. Every deny written to protect a segment under such a policy — including
    /// every one the Store's gate writes from a manifest's <c>ProtectedSegments</c> — is inert.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(WellKnownUsers.Anonymous)]
    [InlineData(WellKnownUsers.Public)]
    public async Task ADenyUnderAPublicReadPolicy_DoesNotWithholdRead(string subject)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        (await CanRead("PolicyOpen/Gated/Page", subject, cancellationToken)).Should().BeTrue(
            "a deny removes a ROLE, and PartitionAccessPolicy.PublicRead is not a role — this fold ORs "
            + "the public grant in after the deny subtraction, so there is nothing for the deny to take "
            + "away. 🚨 This is the MEASURED behaviour and NOT the intended one: the SQL projection's "
            + "own comment says a deny at a longer prefix wins, 'the store-gating shape and it is "
            + "intentional'. So the two read paths disagree, which is the paywall-bypass shape — hidden "
            + "from every listing, readable by exact path — and every deny written to protect a segment "
            + "under such a policy is inert on THIS path (#4716). When the folds are reconciled this "
            + "flips to BeFalse: change it deliberately, do not relax it to keep a run green");
        (await CanRead("PolicyOpen/Cover", subject, cancellationToken)).Should().BeTrue(
            "the control: the policy really is granting public read here, so the reading above is about "
            + "the deny and not about a partition nobody could read in the first place");
    }

    /// <summary>
    /// The second theory, and the reason the false rule looked true: where the public read comes from
    /// root Viewer GRANTS instead of a policy, the SAME deny pair works — readability is a role, and a
    /// deny removes it. This is the shape the rule was written from.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(WellKnownUsers.Anonymous)]
    [InlineData(WellKnownUsers.Public)]
    public async Task ADenyUnderRootGrants_DoesWithholdRead(string subject)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        (await CanRead("GrantOpen/Cover", subject, cancellationToken)).Should().BeTrue(
            "root Viewer grants inherit strictly downward — the cover and every ungated child are "
            + "readable by everyone, which is what makes this a public partition at all");
        (await CanRead("GrantOpen/Gated/Page", subject, cancellationToken)).Should().BeFalse(
            "here the deny DOES hide the content: the read came from a role, so removing the role "
            + "removes the read. Identical denies, opposite outcomes — the difference is whether the "
            + "partition was opened with a policy or with grants, and that is the distinction the "
            + "'a deny beats PublicRead' rule collapsed");
    }

    /// <summary>
    /// The third theory: the one mechanism that suppresses an inherited public grant in THIS fold caps
    /// every subject with it. So it cannot gate a segment somebody has to read — it darkens it for them
    /// too, which is why "just cap the child" is not the remedy for the divergence above.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ADeeperReadCap_SuppressesThePublicGrantAndTheReviewerWithIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        (await CanRead("CapOpen/Gated/Page", WellKnownUsers.Anonymous, cancellationToken)).Should().BeFalse(
            "a deeper Read=false cap is ANDed into the public grant (publicGrant &= scopeCap), so it is "
            + "the only thing in THIS fold that withholds an inherited PublicRead");
        (await CanRead("CapOpen/Gated/Page", Reviewer, cancellationToken)).Should().BeFalse(
            "…and the SAME cap is ANDed into every role-derived permission, so the Viewer holding a "
            + "real grant at the partition root loses Read as well. That is a BLACKOUT, not a gate: it "
            + "cannot express 'closed to the public, open to the people who triage it', which is what a "
            + "submission inbox under a public partition needs (#4716)");
        (await CanRead("CapOpen/Gated/Page", WellKnownUsers.System, cancellationToken)).Should().BeTrue(
            "the one identity the cap does not bind is System, which short-circuits the whole fold. A "
            + "view that reads as the VIEWER — as the Feedback inbox does — therefore goes dark, so "
            + "'just cap it' is not available as the fix");
    }
}
