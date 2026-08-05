using MeshWeaver.Kernel;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Pins the "is this cell's output still current?" rule that drives the markdown cell toolbar's
/// re-run state. The whole point is to stop a result pane from confidently displaying the output of
/// code the reader has since edited — a wrong answer that looks right and that nothing else reports.
/// </summary>
public class CodeCellRunTrackerTest
{
    private static SubmitCodeRequest Cell(string id, string code, string language = "csharp") =>
        new(code) { Id = id, Language = language };

    [Fact]
    public void NeverSubmitted_ReadsAsNeverRun()
    {
        var tracker = new CodeCellRunTracker();
        var current = new[] { Cell("demo", "1 + 1") };

        tracker.StateOf("demo", current).Should().Be(CodeCellRunState.NeverRun);
    }

    [Fact]
    public void SubmittedAndUnchanged_ReadsAsUpToDate()
    {
        var tracker = new CodeCellRunTracker();
        var cell = Cell("demo", "1 + 1");
        tracker.Record(cell);

        tracker.StateOf("demo", new[] { cell }).Should().Be(CodeCellRunState.UpToDate);
    }

    [Fact]
    public void CodeEditedSinceTheRun_ReadsAsStale()
    {
        var tracker = new CodeCellRunTracker();
        tracker.Record(Cell("demo", "1 + 1"));

        tracker.StateOf("demo", new[] { Cell("demo", "1 + 2") })
            .Should().Be(CodeCellRunState.Stale);
    }

    [Fact]
    public void ReRunningAfterAnEdit_ClearsTheStaleState()
    {
        var tracker = new CodeCellRunTracker();
        tracker.Record(Cell("demo", "1 + 1"));
        var edited = Cell("demo", "1 + 2");
        tracker.StateOf("demo", new[] { edited }).Should().Be(CodeCellRunState.Stale);

        tracker.Record(edited);

        tracker.StateOf("demo", new[] { edited }).Should().Be(CodeCellRunState.UpToDate,
            "clicking Run must clear the indicator — an indicator that never clears is noise");
    }

    [Fact]
    public void LanguageSwitch_ReadsAsStale()
    {
        // Same source, different runtime: a Python run of the identical text is a different result,
        // so the previous output no longer belongs to what the cell now says it is.
        var tracker = new CodeCellRunTracker();
        tracker.Record(Cell("demo", "print(1)", "python"));

        tracker.StateOf("demo", new[] { Cell("demo", "print(1)", "csharp") })
            .Should().Be(CodeCellRunState.Stale);
    }

    [Fact]
    public void LineEndingAndTrailingWhitespaceChurn_IsNotStale()
    {
        // An editor rewriting CRLF↔LF, or a save adding a trailing newline, must not cry wolf —
        // an indicator that fires on invisible changes trains readers to ignore it.
        var tracker = new CodeCellRunTracker();
        tracker.Record(Cell("demo", "var x = 1;\r\nvar y = 2;"));

        tracker.StateOf("demo", new[] { Cell("demo", "var x = 1;\nvar y = 2;\n") })
            .Should().Be(CodeCellRunState.UpToDate);
    }

    [Fact]
    public void InteriorWhitespaceChange_IsStale()
    {
        // The normalization must stay narrow: indentation and interior blank lines are real edits
        // in whitespace-significant languages, so they may not be normalized away.
        var tracker = new CodeCellRunTracker();
        tracker.Record(Cell("demo", "if x:\n    print(1)", "python"));

        tracker.StateOf("demo", new[] { Cell("demo", "if x:\n        print(1)", "python") })
            .Should().Be(CodeCellRunState.Stale);
    }

    [Fact]
    public void RecordBatch_TracksEveryCellIndependently()
    {
        var tracker = new CodeCellRunTracker();
        tracker.Record(new[] { Cell("a", "1"), Cell("b", "2") });

        var current = new[] { Cell("a", "1"), Cell("b", "999") };

        tracker.StateOf("a", current).Should().Be(CodeCellRunState.UpToDate);
        tracker.StateOf("b", current).Should().Be(CodeCellRunState.Stale,
            "editing one cell must not implicate its neighbours");
    }

    [Fact]
    public void SubmissionIdLookupIsCaseInsensitive()
    {
        // The id round-trips through an HTML attribute and back; the toolbar marker carries whatever
        // the fence declared, and the rest of the pipeline already compares ids ignoring case.
        var tracker = new CodeCellRunTracker();
        tracker.Record(Cell("MyDemo", "1 + 1"));

        tracker.StateOf("mydemo", new[] { Cell("MyDemo", "1 + 1") })
            .Should().Be(CodeCellRunState.UpToDate);
    }

    [Fact]
    public void CellDeletedFromTheDocument_IsNotStale()
    {
        // The id ran but no longer appears in the parse: there is no code on screen to be stale
        // against, so flagging it would point at nothing.
        var tracker = new CodeCellRunTracker();
        tracker.Record(Cell("gone", "1 + 1"));

        tracker.StateOf("gone", new[] { Cell("other", "2") })
            .Should().Be(CodeCellRunState.UpToDate);
    }

    [Fact]
    public void NullOrEmptyInputs_AreNeverRun_NotStale()
    {
        var tracker = new CodeCellRunTracker();

        tracker.StateOf(null, new[] { Cell("demo", "1") }).Should().Be(CodeCellRunState.NeverRun);
        tracker.StateOf("", null).Should().Be(CodeCellRunState.NeverRun);
        // Recording nothing must not throw — the auto-submit path passes whatever the parse produced.
        tracker.Record((IEnumerable<SubmitCodeRequest>?)null);
        tracker.Record((SubmitCodeRequest?)null);
        tracker.StateOf("demo", null).Should().Be(CodeCellRunState.NeverRun);
    }

    [Fact]
    public void Fingerprint_IsStableAcrossCalls_AndSeparatesLanguageFromCode()
    {
        // Stability is the whole contract: this value is PERSISTED on Code nodes
        // (CodeConfiguration.LastExecutedCodeHash), so a per-process hash would make every node read
        // as stale after the next restart.
        CodeFingerprint.Of("1 + 1", "csharp").Should().Be(CodeFingerprint.Of("1 + 1", "csharp"));
        CodeFingerprint.Of("1 + 1", "csharp").Should().NotBe(CodeFingerprint.Of("1 + 1", "python"));

        // The separator must not be forgeable by shifting the language/code boundary.
        CodeFingerprint.Of("x", "csharp\ny").Should().NotBe(CodeFingerprint.Of("y", "csharp\nx"));
    }

    [Fact]
    public void Fingerprint_CannotBeCollidedByShiftingTheLanguageCodeBoundary()
    {
        // `language` is an unconstrained string, so a delimiter-only encoding is ambiguous:
        // ("b\nc", "a") and ("c", "a\nb") both flatten to "a\nb\nc". Sharing a fingerprint would mean
        // a genuine edit reads as up-to-date — the exact failure this class exists to prevent. The
        // encoding is length-prefixed so the boundary is unambiguous for every input.
        CodeFingerprint.Of("b\nc", "a").Should().NotBe(CodeFingerprint.Of("c", "a\nb"));

        // Same shape one character along, and with the empty language in play.
        CodeFingerprint.Of("xy", "").Should().NotBe(CodeFingerprint.Of("y", "x"));
    }

    [Fact]
    public void Fingerprint_TreatsNullLanguageAsCsharp()
    {
        // "csharp" is the default everywhere else in the path (SubmitCodeRequest.Language,
        // CodeConfiguration.Language); if the fingerprint disagreed, a cell that omitted the
        // language would flip stale the first time something spelled the default out.
        CodeFingerprint.Of("1 + 1", null).Should().Be(CodeFingerprint.Of("1 + 1", "csharp"));
        CodeFingerprint.Of("1 + 1", "  ").Should().Be(CodeFingerprint.Of("1 + 1", "csharp"));
    }

    [Fact]
    public void Fingerprint_TreatsNullCodeAsEmpty()
    {
        CodeFingerprint.Of(null, "csharp").Should().Be(CodeFingerprint.Of("", "csharp"));
    }
}
