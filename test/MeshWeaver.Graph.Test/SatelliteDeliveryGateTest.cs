using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 THE DELIVERY GATE MUST CONSULT THE NODE TYPE'S OWN ACCESS RULE. Issue #3061.
///
/// <para><b>The defect.</b> <c>ValidateDeleteRequest</c> carries
/// <c>[RequiresPermission(Permission.Delete)]</c>, and <c>AccessControlPipeline</c> evaluated it
/// through <c>RequiresPermissionAttribute.GetPermissionChecks</c>, whose default implementation is
/// literally <c>yield return (hubPath, Permission)</c> — a RAW <c>Delete</c> on the satellite's own
/// path. It never consulted <c>INodeTypeAccessRule</c>. Meanwhile
/// <c>SatelliteAccessRule</c> — registered by <c>ActivityNodeType</c> and every other satellite
/// type — maps a satellite's Delete to <c>Permission.Update</c> on its <c>MainNode</c>, because
/// creating a satellite is a modification of its main node and so is removing it.</para>
///
/// <para>#2913 reconciled exactly that mismatch in the delete PRE-FLIGHT
/// (<c>MeshExtensions.CheckDeletePermissionForNode</c>). It did not reconcile the DELIVERY gate,
/// which runs FIRST — so the gate won, and an <c>Editor</c> (Update, no Delete) could publish a
/// satellite and then be refused when erasing it. Measured on production: a recursive delete of
/// <c>Edu/Course</c> refused with <c>Access denied: user 'rbuergi' lacks Delete permission on
/// 'Edu/Course/_Activity/compile-…'</c> for all 72 of its <c>_Activity</c> satellites, leaving an
/// orphan NodeType that could not be removed through any API.</para>
///
/// <para><b>What is pinned here.</b> The three verdicts the gate must reach, on the same mesh with
/// the same three nodes, so that a fix which simply widened Delete would fail two of them:
/// <list type="number">
///   <item><description>an Editor's <c>ValidateDeleteRequest</c> at a SATELLITE is not refused —
///     the rule grants what the raw path check denies;</description></item>
///   <item><description>a Viewer's is still refused — the rule DENIES (no Update on the MainNode),
///     and a rule that refuses leaves the delivery refused;</description></item>
///   <item><description>the same Editor at a GRANT (<c>_Access</c>) is still refused — deleting
///     ordinary content is an edit (Update), but what governs a partition keeps demanding Delete, so
///     nothing widened beyond content. The plain child is now admitted for that reason.</description></item>
/// </list></para>
/// </summary>
public class SatelliteDeliveryGateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "SatelliteDeleteGate";

    /// <summary>The satellite's MainNode — the node its Delete delegates to.</summary>
    private const string MainNodePath = Partition + "/Doc";

    /// <summary>
    /// An <c>Activity</c> satellite, in the shape <c>ActivityNodeType</c> mints: it lives under the
    /// <c>_Activity</c> segment of its main node and its <c>MainNode</c> points AT that main node,
    /// never at itself.
    /// </summary>
    private const string SatellitePath = MainNodePath + "/_Activity/run1";

    /// <summary>An ordinary child of the same main node — a node type with NO access rule.</summary>
    private const string PlainChildPath = MainNodePath + "/Plain";

    private const string EditorUser = "satellite-editor";

    /// <summary>A commenter — Comment on the partition, no Update — and the comment they wrote.</summary>
    private const string CommenterUser = "satellite-commenter";
    private const string OwnCommentPath = MainNodePath + "/_Comment/c1";

    /// <summary>A Partition record below the partition root — rule-governed (PartitionAccessRule demands
    /// Delete) although the rule-less demand for its path would be Update.</summary>
    private const string PartitionRecordPath = Partition + "/Parts/PartX";

    /// <summary>The Editor's own grant — governance, not content.</summary>
    private const string GrantPath = Partition + "/_Access/" + EditorUser + "_Access";
    private const string ViewerUser = "satellite-viewer";

    /// <summary>
    /// 🚨 <see cref="TestTimeouts.Quick"/>, never a literal — CI-scaled, and the only bound in the
    /// suite, so a wait that does not converge reports WHAT it was waiting for rather than being
    /// killed anonymously.
    /// </summary>
    private static TimeSpan Budget => TestTimeouts.Quick;

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter chains PublicAdminAccess(), which
    // grants Public the Admin role in every default partition — under it both identities below
    // would hold Delete outright and every assertion here would pass vacuously.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Partition) { Name = "Satellite Delete Gate", NodeType = "Markdown" },
                new MeshNode("Doc", Partition) { Name = "Doc", NodeType = "Markdown" },
                new MeshNode("Plain", MainNodePath) { Name = "Plain Child", NodeType = "Markdown" },
                new MeshNode("run1", MainNodePath + "/_Activity")
                {
                    Name = "Compile run",
                    NodeType = "Activity",
                    // The satellite pointer. Without it SatelliteAccessRule takes its degenerate
                    // branch and falls back to the standard path check — i.e. the pre-fix answer —
                    // so this line is what makes the case a satellite at all.
                    MainNode = MainNodePath,
                },
                // Update but NOT Delete — the exact entitlement #2913's own rationale names: "an
                // Editor holds Update and not Delete, so an Editor could PUBLISH a satellite and
                // then be refused when erasing it".
                AssignmentNodeFactory.UserRole(EditorUser, "Editor", Partition),
                // Read only — neither Delete on the satellite nor Update on its MainNode.
                AssignmentNodeFactory.UserRole(ViewerUser, "Viewer", Partition),
                AssignmentNodeFactory.UserRole(CommenterUser, "Commenter", Partition),
                new MeshNode("Parts", Partition) { Name = "Parts", NodeType = "Markdown" },
                new MeshNode("PartX", Partition + "/Parts") { Name = "Part X", NodeType = "Partition" },
                new MeshNode("c1", MainNodePath + "/_Comment")
                {
                    Name = "Own comment",
                    NodeType = "Comment",
                    MainNode = MainNodePath,
                    // Identity(...) names the caller by its id, and the rule matches the author by name.
                    Content = new Comment { Author = CommenterUser, PrimaryNodePath = MainNodePath },
                });

    private static AccessContext Identity(string userId) => new()
    {
        ObjectId = userId,
        Name = userId,
    };

    /// <summary>
    /// The pre-flight fan-out's message, posted exactly as <c>PreValidateDescendantsObs</c> posts
    /// it: one <c>ValidateDeleteRequest</c> at the descendant's OWN address, carrying the caller's
    /// AccessContext explicitly (the only copy that survives the scheduler hops). Yields the gate's
    /// <see cref="DeliveryFailure"/>, or null when the delivery was SERVED.
    /// </summary>
    private IObservable<DeliveryFailure?> PreflightRefusal(string path, string userId)
        => RequestHub
            .Observe(new ValidateDeleteRequest(path, MainNodePath),
                o => o.WithTarget(new Address(path)).WithAccessContext(Identity(userId)))
            .Take(1)
            .Select(_ => (DeliveryFailure?)null)
            .Catch((DeliveryFailureException ex) => Observable.Return<DeliveryFailure?>(ex.Failure));

    /// <summary>
    /// 🚨 THE PIN. The satellite's registered rule says its Delete is Update on the MainNode, and
    /// the Editor holds Update there — so the gate must not refuse the pre-flight.
    ///
    /// <para>Pre-fix this failed with <c>Unauthorized</c> / "lacks Delete permission on
    /// 'SatelliteDeleteGate/Doc/_Activity/run1'": the gate folded a raw Delete on the satellite's
    /// own path and never asked the node type.</para>
    /// </summary>
    [Fact]
    public async Task AnEditorsSatellitePreflight_IsNotRefusedByTheGate()
    {
        var failure = await PreflightRefusal(SatellitePath, EditorUser).Should().Within(Budget).Emit();

        failure.Should().BeNull(
            "the Activity type registers SatelliteAccessRule, which maps a satellite's Delete to "
            + "Update on its MainNode — an entitlement this Editor holds. The delivery gate refused "
            + "anyway because it folded a RAW Delete on the satellite's own path and never consulted "
            + "the rule the delete pre-flight already consults (#3061): "
            + (failure?.Message ?? string.Empty));
    }

    /// <summary>
    /// The companion measurement that makes the pin a RULE decision rather than a widening: the same
    /// satellite, the same message, a Viewer instead of an Editor. The rule is consulted and it says
    /// NO — no Update on the MainNode — so the refusal stands.
    /// </summary>
    [Fact]
    public async Task AViewersSatellitePreflight_IsStillRefused()
    {
        var failure = await PreflightRefusal(SatellitePath, ViewerUser).Should().Within(Budget).Emit();

        failure.Should().NotBeNull(
            "a Viewer holds neither Delete on the satellite nor Update on its MainNode, so the "
            + "node type's own rule refuses — consulting the rule must never mean granting");
        failure!.ErrorType.Should().Be(ErrorType.Unauthorized,
            "the rule reached a VERDICT and it is no, so this is a denial and not an availability "
            + "answer: " + failure.Message);
    }

    /// <summary>
    /// The plain child — a node type with NO registered rule — is decided by the standard demand,
    /// <see cref="MeshWeaver.Mesh.Security.DeletePermission.RequiredFor"/>: deleting ordinary content
    /// demands Update, which the Editor holds. (Before deleting content became an edit this case was
    /// the refusal that proved nothing widened outside rule-governed types; the boundary that now
    /// proves it is the GRANT below.)
    /// </summary>
    [Fact]
    public async Task AnEditorsPlainChildPreflight_IsAdmitted_BecauseDeletingContentIsAnEdit()
    {
        var failure = await PreflightRefusal(PlainChildPath, EditorUser).Should().Within(Budget).Emit();

        failure.Should().BeNull(
            "Markdown registers no INodeTypeAccessRule, so the standard demand applies — and deleting "
            + "ordinary content demands Update, which this Editor holds: " + (failure?.Message ?? string.Empty));
    }

    /// <summary>
    /// 🚨 The scope boundary: what GOVERNS a partition is not content. The same Editor's pre-flight at a
    /// grant — <c>_Access</c>, rule-less, under the same partition — is refused, because deleting a
    /// grant still demands Delete, which only an Admin holds. Nothing widened beyond content.
    /// </summary>
    [Fact]
    public async Task AnEditorsGrantPreflight_IsStillRefused()
    {
        var failure = await PreflightRefusal(GrantPath, EditorUser).Should().Within(Budget).Emit();

        failure.Should().NotBeNull(
            "deleting a grant changes who may do what, so it keeps demanding Permission.Delete — and an "
            + "Editor (Update, no Delete) stays refused");
        failure!.ErrorType.Should().Be(ErrorType.Unauthorized,
            "the standard fold reached a verdict: " + failure.Message);
    }

    /// <summary>
    /// 🚨 A delete is re-decided through the type's rule AS A DELETE. Deleting ordinary content
    /// demands Update, which a Commenter lacks, so the standard check denies — and the gate must then
    /// ask the Comment rule about a DELETE (its author may always delete their own comment), not about
    /// an UPDATE, which the rule would refuse.
    /// </summary>
    [Fact]
    public async Task ACommentersOwnCommentPreflight_IsNotRefusedByTheGate()
    {
        var failure = await PreflightRefusal(OwnCommentPath, CommenterUser).Should().Within(Budget).Emit();

        failure.Should().BeNull(
            "SatelliteAccessRule lets a comment's author delete it; the gate must re-decide the denied "
            + "standard check as a Delete, not as the Update the standard demand names: "
            + (failure?.Message ?? string.Empty));
    }

    /// <summary>
    /// 🚨 For a delete-shaped message a GRANT is re-decided too when the node's type declares a Delete
    /// rule. The Editor holds Update, which is the rule-less demand for this path — but the Partition
    /// type's own rule demands Delete, so the version-guarded pre-flight (the branch that would take the
    /// owner's seal) is refused at the gate.
    /// </summary>
    [Fact]
    public async Task AnEditorsVersionGuardedPreflightOnAPartitionRecord_IsRefused()
    {
        var failure = await RequestHub
            .Observe(new ValidateDeleteRequest(PartitionRecordPath) { ExpectedVersion = 1 },
                o => o.WithTarget(new Address(PartitionRecordPath)).WithAccessContext(Identity(EditorUser)))
            .Take(1)
            .Select(_ => (DeliveryFailure?)null)
            .Catch((DeliveryFailureException ex) => Observable.Return<DeliveryFailure?>(ex.Failure))
            .Should().Within(Budget).Emit();

        failure.Should().NotBeNull(
            "the Partition type's rule demands Delete, which an Editor lacks — a grant of the rule-less "
            + "Update demand must not let the pre-flight take the owner's seal");
        failure!.ErrorType.Should().Be(ErrorType.Unauthorized, failure.Message ?? "a denial");
    }
}
