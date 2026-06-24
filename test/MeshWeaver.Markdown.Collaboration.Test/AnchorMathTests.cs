using MeshWeaver.Markdown.Collaboration;
using Xunit;
using static MeshWeaver.Markdown.Collaboration.AnchorMath;

namespace MeshWeaver.Markdown.Collaboration.Test;

/// <summary>
/// Exhaustive tests for <see cref="AnchorMath"/> — the version-delta engine that re-anchors a stored
/// character range onto a newer version of the text. Comments and tracked changes both rely on it.
/// </summary>
public class AnchorMathTests
{
    // ---- Helper: resolve and return the substring the effective range points at ----
    private static string ResolvedText(string anchor, string highlighted, string current)
    {
        var start = anchor.IndexOf(highlighted, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the highlighted text must exist in the anchor");
        var (s, e) = Resolve(anchor, start, highlighted.Length, current);
        return current.Substring(s, e - s);
    }

    // =========================================================================================
    //  Diff
    // =========================================================================================

    [Fact(Timeout = 5000)]
    public void Diff_IdenticalText_IsOneEqualSegment()
    {
        var diff = Diff("hello world", "hello world");
        diff.Should().ContainSingle();
        diff[0].Should().Be(new Segment(Op.Equal, 11));
    }

    [Fact(Timeout = 5000)]
    public void Diff_BothEmpty_IsEmpty()
    {
        Diff("", "").Should().BeEmpty();
    }

    [Fact(Timeout = 5000)]
    public void Diff_PureInsertionAtEnd()
    {
        var diff = Diff("abc", "abcdef");
        diff.Should().Equal(new Segment(Op.Equal, 3), new Segment(Op.Insert, 3));
    }

    [Fact(Timeout = 5000)]
    public void Diff_PureInsertionAtStart()
    {
        var diff = Diff("abc", "xyzabc");
        diff.Should().Equal(new Segment(Op.Insert, 3), new Segment(Op.Equal, 3));
    }

    [Fact(Timeout = 5000)]
    public void Diff_PureDeletionAtStart()
    {
        var diff = Diff("xyzabc", "abc");
        diff.Should().Equal(new Segment(Op.Delete, 3), new Segment(Op.Equal, 3));
    }

    [Fact(Timeout = 5000)]
    public void Diff_Replacement_InTheMiddle()
    {
        var diff = Diff("a big cat", "a red cat");
        diff[0].Should().Be(new Segment(Op.Equal, 2));
        diff[^1].Should().Be(new Segment(Op.Equal, 4));
        diff.Where(s => s.Op == Op.Delete).Sum(s => s.Length).Should().Be(3);
        diff.Where(s => s.Op == Op.Insert).Sum(s => s.Length).Should().Be(3);
    }

    [Fact(Timeout = 5000)]
    public void Diff_FromEmpty_IsPureInsert()
    {
        Diff("", "hello").Should().Equal(new Segment(Op.Insert, 5));
    }

    [Fact(Timeout = 5000)]
    public void Diff_ToEmpty_IsPureDelete()
    {
        Diff("hello", "").Should().Equal(new Segment(Op.Delete, 5));
    }

    [Fact(Timeout = 5000)]
    public void Diff_SegmentLengthsAlwaysAccountForBothTexts()
    {
        var from = "The quick brown fox";
        var to = "The very quick red fox jumps";
        var diff = Diff(from, to);
        diff.Where(s => s.Op != Op.Insert).Sum(s => s.Length).Should().Be(from.Length, "deletes+equals span the old text");
        diff.Where(s => s.Op != Op.Delete).Sum(s => s.Length).Should().Be(to.Length, "inserts+equals span the new text");
    }

    // =========================================================================================
    //  MapIndex
    // =========================================================================================

    [Fact(Timeout = 5000)]
    public void MapIndex_Identity_IsUnchanged()
    {
        var diff = Diff("hello world", "hello world");
        MapIndex(diff, 0).Should().Be(0);
        MapIndex(diff, 6).Should().Be(6);
        MapIndex(diff, 11).Should().Be(11);
    }

    [Fact(Timeout = 5000)]
    public void MapIndex_AfterInsertionAtStart_ShiftsBy()
    {
        var diff = Diff("abc", "xyzabc");
        MapIndex(diff, 0).Should().Be(3);
        MapIndex(diff, 3).Should().Be(6);
    }

    [Fact(Timeout = 5000)]
    public void MapIndex_NegativeClampsToZero_ThenFollowsLeadingInsertion()
    {
        // "abc" → "xabc": a leading 'x' is inserted. A start at index 0 (or below) sticks to the
        // right of that insertion (diff_xIndex), so it maps to 1 — the original first char.
        var diff = Diff("abc", "xabc");
        MapIndex(diff, -5).Should().Be(1);
        MapIndex(diff, 0).Should().Be(1);
        // With left bias (a range end), it stays before the leading insertion.
        MapIndex(diff, 0, biasLeft: true).Should().Be(0);
    }

    [Fact(Timeout = 5000)]
    public void MapIndex_InsideDeletedRegion_CollapsesToDeletionStart()
    {
        var diff = Diff("abcdef", "abf");
        MapIndex(diff, 3).Should().Be(2);
    }

    // =========================================================================================
    //  Resolve — the public surface comments/changes use
    // =========================================================================================

    [Fact(Timeout = 5000)]
    public void Resolve_UnchangedDocument_ReturnsCapturedRange()
    {
        const string text = "The mesh stores nodes per partition.";
        var (s, e) = Resolve(text, 4, 4, text); // "mesh"
        text.Substring(s, e - s).Should().Be("mesh");
    }

    [Fact(Timeout = 5000)]
    public void Resolve_InsertionBeforeRange_ShiftsRangeForward()
    {
        const string anchor = "The mesh stores nodes.";
        const string current = "NOTE: The mesh stores nodes.";
        ResolvedText(anchor, "mesh", current).Should().Be("mesh");
    }

    [Fact(Timeout = 5000)]
    public void Resolve_InsertionAfterRange_LeavesRangeIntact()
    {
        const string anchor = "The mesh stores nodes.";
        const string current = "The mesh stores nodes and edges.";
        ResolvedText(anchor, "mesh", current).Should().Be("mesh");
    }

    [Fact(Timeout = 5000)]
    public void Resolve_DeletionBeforeRange_ShiftsRangeBackward()
    {
        const string anchor = "Intro sentence. The mesh stores nodes.";
        const string current = "The mesh stores nodes.";
        ResolvedText(anchor, "mesh", current).Should().Be("mesh");
    }

    [Fact(Timeout = 5000)]
    public void Resolve_ReplacementBeforeRange_TracksTheRange()
    {
        const string anchor = "A short intro. The mesh stores nodes.";
        const string current = "A considerably longer introduction here. The mesh stores nodes.";
        ResolvedText(anchor, "mesh", current).Should().Be("mesh");
    }

    [Fact(Timeout = 5000)]
    public void Resolve_MultipleEditsAroundRange_StillTracks()
    {
        const string anchor = "One. The mesh stores nodes. Two.";
        const string current = "Zero. One plus. The mesh stores nodes. Two minus. Three.";
        ResolvedText(anchor, "mesh stores", current).Should().Be("mesh stores");
    }

    [Fact(Timeout = 5000)]
    public void Resolve_EditInsideRange_RangeStillCoversChangedText()
    {
        const string anchor = "The quick brown fox.";
        const string current = "The quick red brown fox.";
        var start = anchor.IndexOf("quick brown", StringComparison.Ordinal);
        var (s, e) = Resolve(anchor, start, "quick brown".Length, current);
        var resolved = current.Substring(s, e - s);
        resolved.Should().StartWith("quick");
        resolved.Should().EndWith("brown");
        resolved.Should().Contain("red");
    }

    [Fact(Timeout = 5000)]
    public void Resolve_RangeAtVeryStart()
    {
        const string anchor = "Mesh nodes are partitioned.";
        const string current = "Mesh nodes are partitioned across schemas.";
        var (s, e) = Resolve(anchor, 0, 4, current); // "Mesh"
        current.Substring(s, e - s).Should().Be("Mesh");
    }

    [Fact(Timeout = 5000)]
    public void Resolve_RangeAtVeryEnd()
    {
        const string anchor = "Stored per partition";
        const string current = "Always stored per partition";
        ResolvedText(anchor, "partition", current).Should().Be("partition");
    }

    [Fact(Timeout = 5000)]
    public void Resolve_CaretRange_ZeroLength_IsPreserved()
    {
        const string anchor = "abcdef";
        const string current = "xxabcdef";
        var (s, e) = Resolve(anchor, 3, 0, current);
        e.Should().Be(s);
        s.Should().Be(5); // 'd' shifted by the 2-char insertion
    }

    [Fact(Timeout = 5000)]
    public void Resolve_WholeAnchorDeleted_CollapsesGracefully()
    {
        var (s, e) = Resolve("the highlighted phrase", 4, 11, "completely different content");
        e.Should().BeGreaterThanOrEqualTo(s);
        s.Should().BeInRange(0, "completely different content".Length);
    }

    [Fact(Timeout = 5000)]
    public void Resolve_EmptyCurrent_ReturnsZeroRange()
    {
        var (s, e) = Resolve("anything here", 2, 5, "");
        s.Should().Be(0);
        e.Should().Be(0);
    }

    [Fact(Timeout = 5000)]
    public void Resolve_NullsAreTreatedAsEmpty()
    {
        var (s, e) = Resolve(null, 0, 0, null);
        s.Should().Be(0);
        e.Should().Be(0);
    }

    [Fact(Timeout = 5000)]
    public void Resolve_StartBeyondAnchor_IsClamped()
    {
        var (s, e) = Resolve("short", 100, 5, "short");
        s.Should().Be(5);
        e.Should().Be(5);
    }

    [Theory(Timeout = 5000)]
    [InlineData("prefix ")]
    [InlineData("a much longer prefix that was prepended ")]
    [InlineData("")]
    public void Resolve_VariousPrefixInsertions_KeepTheWord(string prepend)
    {
        const string anchor = "the satellite entities live here";
        var current = prepend + anchor;
        ResolvedText(anchor, "satellite entities", current).Should().Be("satellite entities");
    }

    [Fact(Timeout = 5000)]
    public void Resolve_RealisticParagraphEdit_TracksHighlight()
    {
        const string anchor =
            "MeshWeaver uses an actor-model message hub. UI is reactive Layout Areas rendered in Blazor.";
        const string current =
            "MeshWeaver (the framework) uses an actor-model message hub with address-based partitioning. "
            + "UI is reactive Layout Areas rendered in Blazor Server.";
        ResolvedText(anchor, "actor-model message hub", current).Should().Be("actor-model message hub");
        ResolvedText(anchor, "Layout Areas", current).Should().Be("Layout Areas");
    }

    [Fact(Timeout = 5000)]
    public void Resolve_TwoHighlights_BothTrackIndependently()
    {
        const string anchor = "alpha beta gamma delta";
        const string current = "ZZ alpha beta YY gamma delta WW";
        ResolvedText(anchor, "alpha", current).Should().Be("alpha");
        ResolvedText(anchor, "gamma delta", current).Should().Be("gamma delta");
    }
}
