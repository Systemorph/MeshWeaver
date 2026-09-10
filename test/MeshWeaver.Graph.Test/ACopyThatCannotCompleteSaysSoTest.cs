using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A copy that carried less than the whole subtree reported the count it managed, and
/// completed.</b> The consequence is recorded in
/// <c>Doc/Architecture/MissingDeclaredSources</c>: <c>rbuergi/OperationRequest</c> on
/// memex.meshweaver.cloud — a NodeType node whose entire <c>Source/</c> subtree is absent
/// (<c>search 'namespace:rbuergi/OperationRequest scope:subtree'</c> → <b>0</b>, against
/// <b>21</b> for the package it was copied from) — which then failed its compile on every pod
/// boot for four days, reporting three missing symbols that live in nodes nobody has. The design
/// is <c>Doc/Architecture/CopyCompleteness</c>.
///
/// <para><b>The defect, stated as a property.</b> A copy asserts a set equality it never
/// establishes. <see cref="NodeCopyHelper.CopyNodeTree"/> returned a COUNT of writes that
/// succeeded; nothing anywhere established (a) that the set it enumerated IS the source subtree,
/// or (b) that the set it wrote IS the set it enumerated. Both are set comparisons, and a count
/// can express neither.</para>
///
/// <para><b>Measured on the unfixed helper, on this fixture, 2026-09-10</b> — the two halves are
/// different failures and only the first was silent:</para>
/// <code>
/// A. a descendant the caller may not read
///      subject-scoped enumeration = 5 of the 7 nodes the subtree holds
///      copy RETURNED count = 5                         &lt;- reported success
///      …/Pkg/Restricted        ABSENT                  &lt;- silently left behind
///      …/Pkg/Restricted/Beta   ABSENT
/// B. one write refused in the middle
///      copy THREW "Copy of 'TestData/Pkg/Source' … failed"   &lt;- names ONE of five paths
///      …/Pkg/Source            ABSENT
///      …/Pkg/Source/Alpha      PRESENT                 &lt;- ORPHAN under a parent that never landed
/// </code>
///
/// <para><b>The controls, in both directions.</b>
/// <see cref="AnUnreadableDescendantRefusesTheCopyInsteadOfHalfLandingIt"/> and
/// <see cref="AFailedWriteStopsTheDescentAndNamesEverythingThatDidNotLand"/> are the two
/// regressions; <see cref="AnOrdinaryCopyStillCarriesTheWholeSubtree"/> and
/// <see cref="ACopyOfASubtreeTheCallerCanFullyReadStillSucceeds"/> are the controls a fix that
/// simply refused everything could not pass — the second matters more, because it is the SAME
/// restricted caller running the SAME completeness machinery and succeeding.
/// <see cref="TheCopierReallyCannotReadTheRestrictedBranch"/> pins the premise the first rests on,
/// in both directions, against the fold row-level security itself consults.</para>
/// </summary>
public class ACopyThatCannotCompleteSaysSoTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PkgId = "Pkg";

    /// <summary>The subtree being copied: a root, a readable branch and a restricted branch.</summary>
    private const string SourceRoot = $"{TestPartition}/{PkgId}";
    private const string SourceFolder = $"{SourceRoot}/Source";
    private const string SourceLeaf = $"{SourceFolder}/Alpha";
    private const string RestrictedFolder = $"{SourceRoot}/Restricted";
    private const string RestrictedLeaf = $"{RestrictedFolder}/Beta";

    /// <summary>The five content nodes a complete copy must produce.</summary>
    private static readonly string[] SourcePaths =
        [SourceRoot, SourceFolder, SourceLeaf, RestrictedFolder, RestrictedLeaf];

    /// <summary>
    /// One target namespace PER TEST. The mesh is rebuilt per test, but
    /// <see cref="RefuseOneTargetPathValidator"/> is registered class-wide, so a shared target
    /// would make it fire in the controls too.
    /// </summary>
    private const string UnreadableTarget = $"{TestPartition}/DstUnreadable";
    private const string RefusedWriteTarget = $"{TestPartition}/DstRefusedWrite";
    private const string AdminTarget = $"{TestPartition}/DstAdmin";
    private const string RestrictedButCompleteTarget = $"{TestPartition}/DstRestricted";

    /// <summary>
    /// The path whose CREATE <see cref="RefuseOneTargetPathValidator"/> refuses. It is the FOLDER
    /// of the readable branch, so its child is perfectly creatable — which is what makes the
    /// descent measurable: on the unfixed helper that child landed under a parent that never did.
    /// </summary>
    private const string RefusedPath = $"{RefusedWriteTarget}/{PkgId}/Source";

    /// <summary>
    /// A signed-in caller who may read <see cref="SourceRoot"/> but NOT
    /// <see cref="RestrictedFolder"/>, and who may create under the target namespaces. The exact
    /// shape behind the production orphan: the copier could read the package node and not all of
    /// what hangs off it.
    /// </summary>
    private static readonly AccessContext Copier = new()
    {
        ObjectId = "copier",
        Name = "Copier",
        Email = "copier@example.com",
        Roles = [],
    };

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh — the latter chains PublicAdminAccess(), which
    // grants Public→Admin in every default partition. Under it the Copier would read the restricted
    // branch outright, the refusal would never fire, and this suite would pass having measured
    // nothing.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureServices(services =>
                services.AddScoped<INodeValidator, RefuseOneTargetPathValidator>())
            .AddMeshNodes(
                // Read over the package…
                AssignmentNodeFactory.UserRole(Copier.ObjectId, roleId: "Viewer", scope: SourceRoot),
                // …except this branch. A deny overrides the inherited grant for that role, and the
                // Copier holds no other — which is a gated package's shape.
                AssignmentNodeFactory.UserRole(
                    Copier.ObjectId, roleId: "Viewer", scope: RestrictedFolder, denied: true),
                // …and Create where each test's copy lands.
                AssignmentNodeFactory.UserRole(Copier.ObjectId, roleId: "Admin", scope: UnreadableTarget),
                AssignmentNodeFactory.UserRole(Copier.ObjectId, roleId: "Admin", scope: RefusedWriteTarget),
                AssignmentNodeFactory.UserRole(
                    Copier.ObjectId, roleId: "Admin", scope: RestrictedButCompleteTarget));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static TimeSpan Budget => TestTimeouts.Convergence;

    /// <summary>Creates the five source nodes as the test's own admin identity, parents first.</summary>
    private async Task SeedSource()
    {
        foreach (var path in SourcePaths)
            await NodeFactory
                .CreateNode(MeshNode.FromPath(path) with
                {
                    Name = path, NodeType = "Markdown", State = MeshNodeState.Active,
                })
                .Should().Within(Budget).Emit($"the admin owns {TestPartition}");
    }

    /// <summary>
    /// Switches the ambient viewer to <see cref="Copier"/> and CHECKS THE SWITCH TOOK — without it
    /// a mis-wired identity hop would leave every assertion running as the test's admin, for whom
    /// the whole subtree is readable and "the copy completed" is the correct answer.
    /// </summary>
    private void BecomeTheCopier()
    {
        Access.SetCircuitContext(Copier);
        (Access.Context?.ObjectId ?? Access.CircuitContext?.ObjectId)
            .Should().Be(Copier.ObjectId,
                "every assertion below is about what THIS identity can copy");
    }

    private Task<MeshNode?> Read(string path, string because) =>
        ReadNode(path).Should().Within(Budget).Emit(because);

    private Task<NodeCopyOutcome> CopyOutcome(
        string sourcePath, string targetNamespace, string because) =>
        NodeCopyHelper
            .CopyNodeTreeOutcome(MeshQuery, NodeFactory, Mesh, sourcePath, targetNamespace, force: false)
            .Should().Within(Budget).Emit(because);

    /// <summary>
    /// The premise <see cref="AnUnreadableDescendantRefusesTheCopyInsteadOfHalfLandingIt"/> rests
    /// on, measured against the same fold <c>RlsNodeValidator</c> consults for a Read — in BOTH
    /// directions, so a fold that answered <c>false</c> for everybody would fail HERE rather than
    /// silently validate the refusal.
    /// </summary>
    [Fact]
    public async Task TheCopierReallyCannotReadTheRestrictedBranch()
    {
        await SeedSource();

        (await Mesh.CheckPermission(SourceRoot, Copier.ObjectId, Permission.Read)
                .Should().Within(Budget).Emit("the permission fold answers"))
            .Should().BeTrue("the Copier holds a Viewer grant on the package root");

        (await Mesh.CheckPermission(RestrictedFolder, Copier.ObjectId, Permission.Read)
                .Should().Within(Budget).Emit("the permission fold answers"))
            .Should().BeFalse("the deny at that scope overrides the inherited Viewer grant");

        (await Mesh.CheckPermission(RestrictedFolder, TestUsers.Admin.ObjectId, Permission.Read)
                .Should().Within(Budget).Emit("the permission fold answers"))
            .Should().BeTrue(
                "the branch is readable for the identity that owns it — a fold that denied "
                + "everybody would make the assertion above meaningless");
    }

    /// <summary>
    /// 🚨 THE REGRESSION, first half. The subtree enumeration runs AS THE CALLER, so the two
    /// restricted nodes are simply not in it — and "the caller cannot read them" and "they are not
    /// there" were the same value. On the unfixed helper this copy returned 5 and left the target
    /// package without its Restricted branch, which IS the production orphan.
    /// </summary>
    [Fact]
    public async Task AnUnreadableDescendantRefusesTheCopyInsteadOfHalfLandingIt()
    {
        await SeedSource();
        BecomeTheCopier();

        var outcome = await CopyOutcome(SourceRoot, UnreadableTarget, "the copy answers");

        outcome.Status.Should().Be(NodeCopyStatus.SourceNotFullyReadable,
            "an enumeration that came back short because of permission is NOT an empty subtree, "
            + "and a copy that cannot see all of its source must refuse rather than carry a part");
        (outcome.SourceNodeCount - outcome.EnumeratedCount).Should().Be(2,
            $"'{RestrictedFolder}' and '{RestrictedLeaf}' are held by the subtree and hidden from "
            + "this caller — the System-scoped reading is what turns that into a number instead of "
            + "a silence");
        outcome.IsComplete.Should().BeFalse("a refusal is never a finished copy");
        outcome.Entries.Should().BeEmpty("the refusal is established BEFORE anything is attempted");

        outcome.Describe().Should().Contain(NodeCopyHelper.IncompleteSourceRefusal,
            "the marker every reader of this refusal matches on");
        outcome.Describe().Should().NotContain(RestrictedFolder,
            "a shortfall is a permission fact about the READER — naming the paths it hides would "
            + "turn a refusal into a disclosure surface (#3890)");

        foreach (var relative in SourcePaths.Select(Relative))
            (await Read($"{UnreadableTarget}/{relative}", $"{relative} is read back"))
                .Should().BeNull(
                    "nothing at all is written: a caller who cannot copy the whole subtree does "
                    + "not get half of one");
    }

    /// <summary>
    /// The COUNT surface is derived from the outcome, so it cannot answer with a number for a copy
    /// that was refused. Separate from the test above because a derived surface quietly keeping the
    /// old behaviour is exactly the drift the two-surface shape exists to prevent.
    /// </summary>
    [Fact]
    public async Task TheCountSurfaceErrorsOnARefusalInsteadOfEmittingACount()
    {
        await SeedSource();
        BecomeTheCopier();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            NodeCopyHelper
                .CopyNodeTree(MeshQuery, NodeFactory, Mesh, SourceRoot, UnreadableTarget, force: false)
                .Should().Within(Budget).Emit("the count surface answers"));

        failure.Message.Should().Contain(NodeCopyHelper.IncompleteSourceRefusal,
            "the count surface propagates the outcome's own account rather than a number");
    }

    /// <summary>
    /// 🚨 THE REGRESSION, second half. One refused write used to abort the merge: whatever was
    /// already in flight landed, whatever had not started never did, and the single exception named
    /// one path. Here the refused path is a FOLDER whose child is perfectly creatable — so on the
    /// unfixed helper <c>…/Source/Alpha</c> landed under a <c>…/Source</c> that never existed.
    /// </summary>
    [Fact]
    public async Task AFailedWriteStopsTheDescentAndNamesEverythingThatDidNotLand()
    {
        await SeedSource();

        var outcome = await CopyOutcome(SourceRoot, RefusedWriteTarget, "the copy answers");

        outcome.Status.Should().Be(NodeCopyStatus.Incomplete,
            "a copy that did not carry everything it enumerated is not a copy");

        outcome.Shortfall.Should().Contain(
            e => e.TargetPath == RefusedPath && e.Disposition == NodeCopyDisposition.Failed,
            "the write that was refused is named, with its reason");
        outcome.Shortfall.Should().Contain(
            e => e.TargetPath == $"{RefusedPath}/Alpha"
                 && e.Disposition == NodeCopyDisposition.NotAttempted,
            "AND every descendant it therefore never attempted — the half nobody could see before, "
            + "reported as NOT ATTEMPTED rather than as a failure of its own");

        (await Read($"{RefusedPath}/Alpha", "the would-be orphan is read back"))
            .Should().BeNull(
                "a child of a node that could not be written is an orphan; the descent stops at "
                + "the level that failed instead of scattering it");

        (await Read($"{RefusedWriteTarget}/{PkgId}", "the root is read back"))
            .Should().NotBeNull(
                "the levels that DID land stay — this copy may have overwritten nodes it holds no "
                + "before-image of, so deleting them back would be a second uncontrolled mutation "
                + "rather than a rollback");

        outcome.Entries
            .Where(e => e.Covered)
            .Should().OnlyContain(
                e => outcome.Entries
                    .Where(a => e.TargetPath.StartsWith(a.TargetPath + "/", StringComparison.Ordinal))
                    .All(a => a.Covered),
                "the residue is a well-formed tree: every node that landed has every ancestor that "
                + "landed with it, which is what writing level by level buys");
    }

    /// <summary>
    /// The control for the identity that can read everything: an ordinary complete copy still
    /// copies, and still counts what it copied. The expected count comes from the subtree itself
    /// rather than a literal — the seeded access assignments are nodes of the subtree too, and a
    /// complete copy carries them.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryCopyStillCarriesTheWholeSubtree()
    {
        await SeedSource();

        var held = await MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{SourceRoot} scope:subtree").Complete())
            .Take(1).Select(c => c.Items)
            .Should().Within(Budget).Emit("the admin enumerates the whole subtree");

        var outcome = await CopyOutcome(SourceRoot, AdminTarget, "the copy answers");

        outcome.Status.Should().Be(NodeCopyStatus.Copied, "nothing is hidden from this caller");
        outcome.IsComplete.Should().BeTrue("this is what a finished copy looks like");
        outcome.Shortfall.Should().BeEmpty("every enumerated node landed");
        outcome.CopiedCount.Should().Be(held.Count,
            "a complete copy writes every node the source subtree holds");

        foreach (var relative in SourcePaths.Select(Relative))
            (await Read($"{AdminTarget}/{relative}", $"{relative} is read back"))
                .Should().NotBeNull($"'{relative}' is part of the subtree that was copied");
    }

    /// <summary>
    /// The control that matters most: the SAME restricted caller, copying a subtree it can read in
    /// full, still succeeds. Without it a fix that refused every restricted caller — or every copy
    /// — would pass the refusal tests and look correct.
    /// </summary>
    [Fact]
    public async Task ACopyOfASubtreeTheCallerCanFullyReadStillSucceeds()
    {
        await SeedSource();
        BecomeTheCopier();

        var outcome = await CopyOutcome(
            SourceFolder, RestrictedButCompleteTarget, "the copy answers");

        outcome.Status.Should().Be(NodeCopyStatus.Copied,
            "the whole of THIS subtree is readable to the Copier, so the completeness check has "
            + "nothing to refuse");
        outcome.CopiedCount.Should().Be(2, "the readable branch is a folder and one leaf");

        (await Read($"{RestrictedButCompleteTarget}/Source/Alpha", "the leaf is read back"))
            .Should().NotBeNull("a restricted caller can still copy what it can wholly read");
    }

    /// <summary>The path a source node lands at, relative to the target namespace.</summary>
    private static string Relative(string sourcePath) =>
        sourcePath[(TestPartition.Length + 1)..];

    /// <summary>
    /// Refuses the CREATE of exactly one path. A real <see cref="INodeValidator"/> registered in a
    /// real mesh — the framework's own extension point, not a stand-in for one — because the
    /// failure being reproduced is "one write in the middle of a subtree is refused" and it has to
    /// happen at a path whose CHILD is still creatable. Stateless: it holds a constant.
    /// </summary>
    private sealed class RefuseOneTargetPathValidator : INodeValidator
    {
        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } =
            [NodeOperation.Create];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context) =>
            Observable.Return(
                string.Equals(context.Node?.Path, RefusedPath, StringComparison.Ordinal)
                    ? NodeValidationResult.Invalid(
                        $"refused by {nameof(RefuseOneTargetPathValidator)}")
                    : NodeValidationResult.Valid());
    }
}
