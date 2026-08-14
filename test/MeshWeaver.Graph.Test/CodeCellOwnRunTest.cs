using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the rule that decides whether a code cell shows any output at all
/// (<c>CodeLayoutAreas.ShowsOwnRun</c>): the cell renders the viewer's OWN last run, never
/// somebody else's.
///
/// <para>The defect this closes: <c>LastActivityPath</c> is one field on a SHARED node, and a run
/// writes its activity into the runner's own partition. A reader who is not the runner therefore
/// found a pointer into a stranger's partition — greeting them with a <c>✓ Done</c> for work they
/// never did, or, when they could not read it, taking the whole cell down with
/// <c>UnauthorizedAccessException</c> so the example rendered as "Access denied". A learner's
/// installed COPY inherits the field wholesale, which is how a freshly installed course showed
/// exactly that.</para>
/// </summary>
public class CodeCellOwnRunTest
{
    [Fact]
    public void MyOwnRun_IsShown()
        => CodeLayoutAreas.ShowsOwnRun("alice", "alice/_Activity/abc123").Should().BeTrue(
            "a cell shows the output of the run this viewer started");

    [Fact]
    public void SomebodyElsesRun_IsNot()
        => CodeLayoutAreas.ShowsOwnRun("alice", "e2e-admin/_Activity/abc123").Should().BeFalse(
            "the author's run is not the learner's — this is the inherited-copy case, and " +
            "rendering it is either a foreign '✓ Done' or an Access-denied cell");

    [Fact]
    public void NoRunRecorded_ShowsNothing()
    {
        CodeLayoutAreas.ShowsOwnRun("alice", null).Should().BeFalse();
        CodeLayoutAreas.ShowsOwnRun("alice", "").Should().BeFalse();
        CodeLayoutAreas.ShowsOwnRun("alice", "   ").Should().BeFalse();
    }

    [Fact]
    public void UnresolvedViewer_ShowsNothing()
    {
        // Anonymous, system, or a hub principal — ResolveViewerHome yields null, and "we do not
        // know who is reading" must never resolve to "show them the last person's run".
        CodeLayoutAreas.ShowsOwnRun(null, "alice/_Activity/abc123").Should().BeFalse();
        CodeLayoutAreas.ShowsOwnRun("", "alice/_Activity/abc123").Should().BeFalse();
    }

    [Fact]
    public void PathWithoutAPartition_ShowsNothing()
    {
        // A bare id names no partition; treating it as a match would let a malformed pointer
        // through the one gate that exists.
        CodeLayoutAreas.ShowsOwnRun("alice", "alice").Should().BeFalse();
        CodeLayoutAreas.ShowsOwnRun("alice", "/alice/_Activity/abc").Should().BeFalse();
    }

    [Fact]
    public void PartitionMatchIsWholeSegment_NotAPrefix()
        // "alice" must not match "alicia/..." or "alice2/..." — a prefix comparison would hand one
        // user another user's activity whenever their names share a stem.
        => CodeLayoutAreas.ShowsOwnRun("alice", "alice2/_Activity/abc").Should().BeFalse();

    [Fact]
    public void CasingOfTheUserIdDoesNotDecide()
        // User ids round-trip through claims and paths with inconsistent casing; a case-sensitive
        // compare would hide a viewer's own run from them.
        => CodeLayoutAreas.ShowsOwnRun("Alice", "alice/_Activity/abc").Should().BeTrue();
}
