using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>THE ANSWER for an anonymous reader against somebody else's SUBMISSION, with one case on each
/// side of the change</b> — MeshWeaver#4716.
///
/// <para>A partition that collects user submissions publishes a catalog half (issue mirrors, source,
/// releases) and holds an inbox satellite. The two shapes below differ in ONE node — how the public
/// half is opened — and that one node decides whether a logged-out visitor can read a submission
/// filed by somebody else:</para>
/// <list type="bullet">
///   <item><b>OpenPkg</b> — opened with <c>PartitionAccessPolicy { PublicRead = true }</c>, the shape
///     <c>PackageInstaller.EnsureDeclaredAccess</c> wrote for every pre-installed partition before this
///     change, WITH the Public/Anonymous deny pair the plugin machinery writes for a declared
///     <c>ProtectedSegments</c> entry sitting on the inbox. <b>The submission is READABLE.</b> This is
///     the case that would have caught it and the one the fix exists for: the protection is present,
///     intact, and does nothing.</item>
///   <item><b>GatedPkg</b> — opened with root Public+Anonymous Viewer GRANTS and a <c>_Policy</c> that
///     WITHHOLDS public read, the shape core writes now. Identical deny pair, identical catalog half.
///     <b>The submission is not readable, and everything else still is.</b></item>
/// </list>
///
/// <para><b>Why the pair is the test and neither half alone is.</b> Read on its own, GatedPkg's denial
/// is equally consistent with "nothing in that partition was readable anyway" — so each shape carries
/// its own public-half control, asserted in the same fixture against the same evaluator. And the
/// reviewer + System readings on GatedPkg are what separate a GATE from a BLACKOUT: a deeper
/// <c>Read = false</c> cap would also deny the anonymous visitor, and would take the people who triage
/// the inbox down with it (<c>PublicReadIsNotSuppressedByADenyTest</c> measures that arm).</para>
///
/// <para><b>Scope of the measurement.</b> This is the C# <c>PermissionEvaluator</c> against a real
/// monolith mesh. The SQL fold is NOT executed here and cannot be — core has no Postgres test lane.
/// On that path the same OpenPkg shape answers the OPPOSITE way: the projection emits the policy as
/// allow-<c>Read</c> rows for <c>Public</c>/<c>Anonymous</c> at the partition prefix and the read-side
/// fold resolves <c>DISTINCT ON (user_id) … ORDER BY LENGTH(node_path_prefix) DESC</c>, so the deeper
/// deny row wins (<c>AccessControlQueryTests.PaywalledContent_StaysInvisibleToAnonymous</c>, in
/// MeshWeaver.Plugins, pins that against a real Postgres). One shape, two answers, is the whole reason
/// the remedy is to stop using it rather than to pick a winner: GatedPkg's grants and denies are ROLE
/// rows, which both folds resolve by the same longest-prefix rule.</para>
/// </summary>
public class AnonymousCannotReadAProtectedSubmissionTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>A real signed-in subject holding Viewer at the partition root — the inbox's triager.</summary>
    private const string Reviewer = "protected-segment-reviewer";

    /// <summary>The submission under test — filed by somebody who is not the reader.</summary>
    private const string OpenSubmission = "OpenPkg/_Submissions/theirs";

    private const string GatedSubmission = "GatedPkg/_Submissions/theirs";

    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin,
    // which would make every reading below Read regardless of the access model.
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            // ── The shape core wrote before this change: a blanket PublicRead policy over the
            //    partition, with the inbox's declared deny pair under it ─────────────────────────
            new MeshNode("OpenPkg") { NodeType = "Markdown" },
            AssignmentNodeFactory.Policy("OpenPkg", new PartitionAccessPolicy { PublicRead = true }),
            new MeshNode("Cover", "OpenPkg") { NodeType = "Markdown" },
            new MeshNode("_Submissions", "OpenPkg") { NodeType = "Markdown" },
            AssignmentNodeFactory.UserRole(
                WellKnownUsers.Public, "Viewer", "OpenPkg/_Submissions", denied: true),
            AssignmentNodeFactory.UserRole(
                WellKnownUsers.Anonymous, "Viewer", "OpenPkg/_Submissions", denied: true),
            new MeshNode("theirs", "OpenPkg/_Submissions") { NodeType = "Markdown" },

            // ── The shape core writes now: root grants carry the publication, the policy withholds
            //    public read, the same deny pair gates the inbox ──────────────────────────────────
            new MeshNode("GatedPkg") { NodeType = "Markdown" },
            AssignmentNodeFactory.Policy("GatedPkg", new PartitionAccessPolicy { PublicRead = false }),
            AssignmentNodeFactory.UserRole(WellKnownUsers.Public, "Viewer", "GatedPkg"),
            AssignmentNodeFactory.UserRole(WellKnownUsers.Anonymous, "Viewer", "GatedPkg"),
            AssignmentNodeFactory.UserRole(Reviewer, "Viewer", "GatedPkg"),
            new MeshNode("Cover", "GatedPkg") { NodeType = "Markdown" },
            new MeshNode("_Submissions", "GatedPkg") { NodeType = "Markdown" },
            AssignmentNodeFactory.UserRole(
                WellKnownUsers.Public, "Viewer", "GatedPkg/_Submissions", denied: true),
            AssignmentNodeFactory.UserRole(
                WellKnownUsers.Anonymous, "Viewer", "GatedPkg/_Submissions", denied: true),
            new MeshNode("theirs", "GatedPkg/_Submissions") { NodeType = "Markdown" });

    /// <summary>
    /// The effective permission a SUBJECT holds on a path. Asserts the FIRST decision, so an initial
    /// leak cannot hide behind a later matching answer.
    /// </summary>
    private async Task<bool> CanRead(string path, string subject, CancellationToken cancellationToken)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var context = new AccessContext { ObjectId = subject, Name = subject };
        access.SetContext(context);
        access.SetHostIdentity(context);
        var permission = await Mesh.GetEffectivePermissions(path, subject)
            .FirstAsync().Timeout(TestTimeouts.Quick).Await(cancellationToken);
        Output.WriteLine($"{subject} on {path}: {permission}");
        return permission.HasFlag(Permission.Read);
    }

    /// <summary>
    /// 🚨 <b>THE EXPOSURE, as a passing test.</b> Under a blanket <c>PublicRead</c> policy a logged-out
    /// visitor reads a submission that carries their own subject's Viewer DENY. Nothing is misconfigured
    /// in this fixture — the deny pair is exactly what the declaration asks for and exactly what the
    /// plugin machinery writes. The policy is what makes it inert.
    ///
    /// <para>🚨 When this flips it must flip because the SHAPE stopped being written, not because the
    /// assertion was relaxed: it is the negative half of a pair, and its partner
    /// (<see cref="TheGrantShape_WithholdsTheSubmissionAndKeepsTheCoverPublic"/>) is what the product
    /// relies on.</para>
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(WellKnownUsers.Anonymous)]
    [InlineData(WellKnownUsers.Public)]
    public async Task ThePolicyShape_PublishesTheSubmissionDespiteItsDeny(string subject)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        (await CanRead("OpenPkg/Cover", subject, cancellationToken)).Should().BeTrue(
            "the control for the reading below: the partition really is published, so a denial there "
            + "would be about the deny and not about a partition nobody could read");
        (await CanRead(OpenSubmission, subject, cancellationToken)).Should().BeTrue(
            "MEASURED, and the defect: PartitionAccessPolicy.PublicRead is ORed into the effective "
            + "permission AFTER the per-subject deny subtraction, and it is not a role, so the deny on "
            + "the inbox has nothing to take away. A submission filed by somebody else is served to a "
            + "logged-out reader with the protection sitting right there — and only on this read path, "
            + "which is what made it invisible: the SQL fold's longest-prefix scan denies the same node, "
            + "so every listing looked correctly gated (MeshWeaver#4716)");
    }

    /// <summary>
    /// The other side of the change: the same inbox, the same deny pair, the publication moved onto root
    /// Viewer GRANTS and the policy withholding public read. The submission is withheld from both
    /// well-known subjects and the catalog half is untouched.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(WellKnownUsers.Anonymous)]
    [InlineData(WellKnownUsers.Public)]
    public async Task TheGrantShape_WithholdsTheSubmissionAndKeepsTheCoverPublic(string subject)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        (await CanRead("GatedPkg/Cover", subject, cancellationToken)).Should().BeTrue(
            "root Public/Anonymous Viewer grants inherit strictly downward, so the catalog half stays "
            + "world-readable — the publication the package asked for is NOT what is being withdrawn");
        (await CanRead(GatedSubmission, subject, cancellationToken)).Should().BeFalse(
            "THE assertion: with the read coming from a ROLE, the deny on the inbox removes the role "
            + "and the submission is withheld. Same deny nodes as OpenPkg, opposite answer — the only "
            + "difference is which mechanism opened the partition, and this one is resolved the same "
            + "way by the C# evaluator and by the SQL longest-prefix fold");
    }

    /// <summary>
    /// A gate, not a blackout: the deny names only the two well-known subjects, so the reviewer who
    /// triages the inbox keeps reading it, and System — which every submit path impersonates — keeps
    /// writing. That pair is the property a deeper <c>Read = false</c> cap cannot deliver, which is why
    /// "just cap the segment" is not the remedy.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheGrantShape_LeavesTheReviewerAndSystemReadingTheInbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        (await CanRead(GatedSubmission, Reviewer, cancellationToken)).Should().BeTrue(
            "the triager holds their own Viewer grant at the partition root and no deny names them, so "
            + "the inbox is theirs to read — a deeper Read=false cap would have taken it away from them "
            + "together with the public");
        (await CanRead(GatedSubmission, WellKnownUsers.System, cancellationToken)).Should().BeTrue(
            "and System short-circuits the fold, so the submit path — which impersonates System exactly "
            + "so a low-privilege user can file without holding write access — is unaffected by the "
            + "gate. Submittable and not publicly readable is the shape a feedback inbox needs");
    }
}
