using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the rule that decides whether a code cell renders an output pane at all
/// (<c>CodeLayoutAreas.ShowsRecordedRun</c>).
///
/// <para>The defect it closes: <c>LastActivityPath</c> is one field on a node, and a run writes its
/// activity into the RUNNER's own partition. A learner's installed COPY inherits that pointer
/// wholesale — so their copy pointed at the AUTHOR's activity and either greeted them with a
/// <c>✓ Done</c> for work they never did, or, when they could not read it, threw and took the whole
/// cell down ("Access denied" where the example should be).</para>
///
/// <para>The rule is deliberately NOT "only my own run": a shared page's output renders for every
/// reader, anonymous included, and <c>CodeCellOutputCaptureTest</c> pins that public path. The
/// distinction is whose COPY the cell is — which is why every case below names the node's partition
/// as well as the viewer's.</para>
/// </summary>
public class CodeCellOwnRunTest
{
    private const string SharedCell = "RiskTransfer/01-GrossToNet/Source/L1LossBook";
    private const string LearnersCopy = "alice/RiskTransfer/01-GrossToNet/Source/L1LossBook";

    [Fact]
    public void MyOwnRun_IsShown_WhereverTheCellLives()
    {
        CodeLayoutAreas.ShowsRecordedRun("alice", SharedCell, "alice/_Activity/abc").Should().BeTrue(
            "the viewer started this run — on a shared cell it is still theirs to see");
        CodeLayoutAreas.ShowsRecordedRun("alice", LearnersCopy, "alice/_Activity/abc").Should().BeTrue(
            "and the same holds in their own copy, which is the normal case after they press Run");
    }

    [Fact]
    public void ForeignRun_InMyOwnCopy_IsHidden()
        => CodeLayoutAreas.ShowsRecordedRun("alice", LearnersCopy, "e2e-admin/_Activity/abc")
            .Should().BeFalse(
                "a pointer into someone else's partition inside ALICE's own copy can only be an " +
                "artifact of the copy — this is the inherited-run defect, and it renders as either " +
                "a foreign '✓ Done' or an Access-denied cell");

    [Fact]
    public void ForeignRun_OnASharedCell_IsStillShown()
    {
        // 🚨 The case CI caught: a Doc/course page's last run renders for OTHER readers, including
        // anonymous ones, over the partition's public-read path (CodeCellOutputCaptureTest pins it).
        // Hiding it would blank every shared example's output on every mesh.
        CodeLayoutAreas.ShowsRecordedRun("alice", SharedCell, "e2e-admin/_Activity/abc")
            .Should().BeTrue("a shared cell keeps showing what it recorded");
        CodeLayoutAreas.ShowsRecordedRun(null, SharedCell, "e2e-admin/_Activity/abc")
            .Should().BeTrue("…and an anonymous reader is exactly who that public path serves");
    }

    [Fact]
    public void NoRunRecorded_ShowsNothing()
    {
        CodeLayoutAreas.ShowsRecordedRun("alice", LearnersCopy, null).Should().BeFalse();
        CodeLayoutAreas.ShowsRecordedRun("alice", LearnersCopy, "").Should().BeFalse();
        CodeLayoutAreas.ShowsRecordedRun("alice", LearnersCopy, "   ").Should().BeFalse();
    }

    [Fact]
    public void ActivityPathWithoutAPartition_ShowsNothing()
    {
        // A bare id names no partition; letting it through would defeat the one gate there is.
        CodeLayoutAreas.ShowsRecordedRun("alice", LearnersCopy, "abc").Should().BeFalse();
        CodeLayoutAreas.ShowsRecordedRun("alice", LearnersCopy, "/alice/_Activity/abc").Should().BeFalse();
    }

    [Fact]
    public void PartitionMatchIsWholeSegment_NotAPrefix()
        // "alice" must not match "alice2/…" — a prefix compare would hand one user another's run
        // whenever their names share a stem.
        => CodeLayoutAreas.ShowsRecordedRun("alice", LearnersCopy, "alice2/_Activity/abc")
            .Should().BeFalse();

    [Fact]
    public void CasingOfTheUserIdDoesNotDecide()
    {
        // Ids round-trip through claims and paths with inconsistent casing; a case-sensitive compare
        // would hide a viewer's own run from them.
        CodeLayoutAreas.ShowsRecordedRun("Alice", LearnersCopy, "alice/_Activity/abc").Should().BeTrue();
        CodeLayoutAreas.ShowsRecordedRun("alice", "Alice/RiskTransfer/L/Source/C", "bob/_Activity/abc")
            .Should().BeFalse("the copy is still hers when the path is cased differently");
    }
}
