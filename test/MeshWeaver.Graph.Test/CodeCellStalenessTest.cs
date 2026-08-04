using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the Code node cell's "the output below belongs to code you have since edited" rule
/// (<c>CodeLayoutAreas.IsOutputStale</c>), which drives the amber toolbar and the re-run glyph.
/// <para>The rule must only claim staleness it can PROVE: a false amber on every node would be worse
/// than no indicator, because the one place it matters would be indistinguishable from the noise.</para>
/// </summary>
public class CodeCellStalenessTest
{
    private static CodeConfiguration Ran(string code, string? hash, string language = "csharp") =>
        new()
        {
            Code = code,
            Language = language,
            IsExecutable = true,
            LastExecutedAt = DateTimeOffset.UtcNow,
            LastExecutedCodeHash = hash,
        };

    [Fact]
    public void CodeUnchangedSinceTheRun_IsNotStale()
    {
        var config = Ran("1 + 1", CodeFingerprint.Of("1 + 1", "csharp"));

        CodeLayoutAreas.IsOutputStale(config).Should().BeFalse();
    }

    [Fact]
    public void CodeEditedSinceTheRun_IsStale()
    {
        var config = Ran("1 + 2", CodeFingerprint.Of("1 + 1", "csharp"));

        CodeLayoutAreas.IsOutputStale(config).Should().BeTrue();
    }

    [Fact]
    public void LanguageSwitchedSinceTheRun_IsStale()
    {
        // Same text, different runtime — the previous output came from somewhere else entirely.
        var config = Ran("print(1)", CodeFingerprint.Of("print(1)", "csharp"), language: "python");

        CodeLayoutAreas.IsOutputStale(config).Should().BeTrue();
    }

    [Fact]
    public void NeverRun_IsNotStale()
    {
        // Nothing has run, so there is no output to be stale — the cell shows "Not yet run." instead.
        var config = new CodeConfiguration { Code = "1 + 1", IsExecutable = true };

        CodeLayoutAreas.IsOutputStale(config).Should().BeFalse();
    }

    [Fact]
    public void RanBeforeTheHashExisted_IsNotStale()
    {
        // 🚨 The migration case: every Code node executed by a build older than
        // LastExecutedCodeHash has LastExecutedAt but no hash. Treating "unknown" as stale would
        // light up every such node on every mesh at once, which is exactly how an indicator becomes
        // background noise nobody reads.
        CodeLayoutAreas.IsOutputStale(Ran("1 + 1", hash: null)).Should().BeFalse();
        CodeLayoutAreas.IsOutputStale(Ran("1 + 1", hash: "")).Should().BeFalse();
    }

    [Fact]
    public void NullConfiguration_IsNotStale()
    {
        CodeLayoutAreas.IsOutputStale(null).Should().BeFalse();
    }

    [Fact]
    public void WhitespaceOnlyReformatting_IsNotStale()
    {
        // Line-ending churn from an editor or a GitSync round-trip is not an edit the reader made.
        var config = Ran("var x = 1;\r\n", CodeFingerprint.Of("var x = 1;", "csharp"));

        CodeLayoutAreas.IsOutputStale(config).Should().BeFalse();
    }

    // ── The toolbar actually RENDERS the state ──────────────────────────────────────────────────
    // IsOutputStale being right is worth nothing if the toolbar ignores it. These pin the wiring:
    // the rule reaches the control tree, as a chip the reader can read and a tint they can see.

    private static StackControl Toolbar(CodeConfiguration config) =>
        (StackControl)CodeLayoutAreas.BuildCellToolbar(
            new Address("Course/Source/Cell"), config, isExecutable: true,
            language: config.Language, lastActivity: null, canEdit: false);

    private static bool HasArea(StackControl toolbar, string area) =>
        toolbar.Areas.Any(a => Equals(a.Id, area));

    private static CodeConfiguration Stale => Ran("1 + 2", CodeFingerprint.Of("1 + 1", "csharp"));
    private static CodeConfiguration Fresh => Ran("1 + 1", CodeFingerprint.Of("1 + 1", "csharp"));

    [Fact]
    public void StaleCell_ToolbarCarriesTheReRunChipAndTheWarningTint()
    {
        var toolbar = Toolbar(Stale);

        toolbar.Style?.ToString().Should().Contain("--warning-fill-rest",
            "a stale cell's toolbar is tinted amber, like the NodeType editor's needs-compile panel");
        HasArea(toolbar, CodeLayoutAreas.StaleChipArea).Should().BeTrue(
            "the reader needs words, not only a colour, to know why the bar changed");
    }

    [Fact]
    public void FreshCell_HasNoChipAndKeepsTheNeutralToolbar()
    {
        var toolbar = Toolbar(Fresh);

        toolbar.Style?.ToString().Should().Contain("--neutral-layer-2").And.NotContain("--warning");
        HasArea(toolbar, CodeLayoutAreas.StaleChipArea).Should().BeFalse(
            "an up-to-date cell must look ordinary — otherwise the signal is noise");
    }

    [Fact]
    public void EitherWay_TheRunButtonIsStillThere()
    {
        // The stale treatment must never cost the reader the control they need — the whole point of
        // the indicator is to get them to press it.
        HasArea(Toolbar(Stale), CodeLayoutAreas.RunButtonArea).Should().BeTrue();
        HasArea(Toolbar(Fresh), CodeLayoutAreas.RunButtonArea).Should().BeTrue();
    }

    [Fact]
    public void RunGlyph_IsTheReRunArrowWhenStale_ThePlayTriangleOtherwise()
    {
        // The LABEL stays "Run" (readers and e2e suites both find the control by that word); only the
        // glyph carries the state, so nothing moves or gets renamed under the user.
        CodeLayoutAreas.RunGlyph(isStale: true).Id.Should().Be("ArrowSync");
        CodeLayoutAreas.RunGlyph(isStale: false).Id.Should().Be("Play");
    }
}
