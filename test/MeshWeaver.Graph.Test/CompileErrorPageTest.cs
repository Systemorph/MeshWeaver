using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Dedicated tests for the compile-error page that <see cref="NodeTypeLayoutAreas.Progress"/>
/// renders when a NodeType's <see cref="CompilationStatus"/> is <see cref="CompilationStatus.Error"/>.
///
/// <para>The contract (the thing that makes a failed compile APPARENT instead of an indefinite
/// spinner): for each affected source file we get back, IN ORDER, a markdown LINK to the Code
/// node followed by a read-only Monaco <see cref="CodeEditorControl"/> whose
/// <see cref="CodeEditorControl.Diagnostics"/> MARK each Roslyn error at its exact line/column —
/// the IDE-style error overlay. The structured diagnostics are kept in their native per-file
/// <see cref="DiagnosticInfo"/> form (id, severity, message, position), never flattened, so the
/// markers land precisely where the compiler flagged them.</para>
///
/// <para>These exercise the pure builder <see cref="NodeTypeLayoutAreas.BuildCompileErrorSourceViews"/>
/// directly (deterministic, no layout-area round-trip) so ordering and content are asserted
/// without coupling to the reactive rendering plumbing.</para>
/// </summary>
public class CompileErrorPageTest
{
    private const string SourcePath = "Acme/Widget/Source/Widget.cs";

    private static DiagnosticInfo Diag(string id, DiagnosticSeverity severity, string message,
        string sourcePath, int startLine, int startCol, int endLine, int endCol) =>
        new(id, severity, message,
            new SourceLocation(sourcePath,
                new SourceRange(new SourcePosition(startLine, startCol), new SourcePosition(endLine, endCol))));

    private static NodeTypeDefinition Errored(params DiagnosticInfo[] diagnostics) =>
        new()
        {
            CompilationStatus = CompilationStatus.Error,
            CompilationError = "Compilation failed for 'Acme/Widget': CS0234 ...",
            CompilationDiagnostics = ImmutableList.CreateRange(diagnostics),
        };

    [Fact]
    public void SingleFile_Emits_OrderedLink_ThenReadonlyEditor_WithMarkedDiagnostics()
    {
        var def = Errored(
            Diag("CS0234", DiagnosticSeverity.Error,
                "The type or namespace name 'Charting' does not exist in the namespace 'MeshWeaver'",
                SourcePath, startLine: 0, startCol: 13, endLine: 0, endCol: 21),
            Diag("CS0103", DiagnosticSeverity.Error,
                "The name 'Chart' does not exist in the current context",
                SourcePath, startLine: 9, startCol: 11, endLine: 9, endCol: 16));

        var views = NodeTypeLayoutAreas.BuildCompileErrorSourceViews(def);

        // Ordered: one markdown link followed by one editor for the single affected file.
        views.Should().HaveCount(2, "one source link + one marked editor for the single failing file");
        views[0].Should().BeOfType<MarkdownControl>("the link to the source comes first");
        views[1].Should().BeOfType<CodeEditorControl>("the marked editor comes after its link");

        // Clear, linked error: the markdown links straight to the Code node and names the file.
        var link = ((MarkdownControl)views[0]).Markdown?.ToString();
        link.Should().Contain("Widget.cs", "the link is labelled with the source file name");
        link.Should().Contain($"](/{SourcePath})", "the link navigates to the source Code node so the user can fix it");

        // Marked error overlay: the editor carries the captured diagnostics as Monaco markers,
        // read-only, C#, at the exact positions Roslyn reported.
        var editor = (CodeEditorControl)views[1];
        editor.Readonly.Should().Be(true, "the compile-error editor is read-only");
        editor.Language.Should().Be("csharp");
        editor.Diagnostics.Should().NotBeNull();
        editor.Diagnostics!.Should().HaveCount(2, "both diagnostics for this file become markers");
        editor.Diagnostics.Should().Contain(m =>
            m.Code == "CS0234" && m.StartLine == 0 && m.StartCharacter == 13 && m.Severity == (int)DiagnosticSeverity.Error,
            "the missing-Charting error is marked at its exact line/column");
        editor.Diagnostics.Should().Contain(m =>
            m.Code == "CS0103" && m.StartLine == 9,
            "the unknown-Chart error is marked on its line");
    }

    [Fact]
    public void MultipleFiles_GroupedPerFile_OrdinalOrdered_LinkThenEditorEach()
    {
        const string fileB = "Acme/Widget/Source/B.cs";
        const string fileA = "Acme/Widget/Source/A.cs";
        // Intentionally add B before A — the builder must group + ORDER by path so the page is deterministic.
        var def = Errored(
            Diag("CS0103", DiagnosticSeverity.Error, "err in B", fileB, 2, 0, 2, 4),
            Diag("CS0246", DiagnosticSeverity.Error, "err in A", fileA, 1, 0, 1, 4));

        var views = NodeTypeLayoutAreas.BuildCompileErrorSourceViews(def);

        views.Should().HaveCount(4, "link + editor per file, two files");
        // A before B (ordinal), each as link-then-editor.
        ((MarkdownControl)views[0]).Markdown!.ToString().Should().Contain($"](/{fileA})");
        views[1].Should().BeOfType<CodeEditorControl>();
        ((CodeEditorControl)views[1]).Diagnostics!.Should().ContainSingle(m => m.Code == "CS0246");
        ((MarkdownControl)views[2]).Markdown!.ToString().Should().Contain($"](/{fileB})");
        views[3].Should().BeOfType<CodeEditorControl>();
        ((CodeEditorControl)views[3]).Diagnostics!.Should().ContainSingle(m => m.Code == "CS0103");
    }

    [Fact]
    public void NoStructuredDiagnostics_EmitsNothing()
    {
        // A pre-capture failed compile (only the flat CompilationError summary) yields no editors —
        // the flat summary is rendered separately; this builder is a no-op.
        NodeTypeLayoutAreas.BuildCompileErrorSourceViews(new NodeTypeDefinition()).Should().BeEmpty();
        NodeTypeLayoutAreas.BuildCompileErrorSourceViews(
            new NodeTypeDefinition { CompilationStatus = CompilationStatus.Error, CompilationError = "boom" })
            .Should().BeEmpty();
    }

    [Fact]
    public void LocationlessDiagnostics_AreExcluded_FromTheMarkedEditors()
    {
        // Assembly-level diagnostics (no source location) can't be marked in a file — they belong to
        // the flat summary, not an editor. Only located diagnostics produce editors.
        var def = Errored(
            new DiagnosticInfo("CS5001", DiagnosticSeverity.Error, "no entry point", Location: null),
            Diag("CS0103", DiagnosticSeverity.Error, "located", SourcePath, 3, 0, 3, 4));

        var views = NodeTypeLayoutAreas.BuildCompileErrorSourceViews(def);

        views.Should().HaveCount(2, "only the one LOCATED diagnostic's file produces a link + editor");
        ((CodeEditorControl)views[1]).Diagnostics!.Should().ContainSingle(m => m.Code == "CS0103");
    }

    // ── The per-INSTANCE emergency overlay page (NodeTypeEnrichmentHelpers) ──
    // When you navigate to an INSTANCE of a broken NodeType, its Overview area comes
    // back as the emergency overlay built by BuildCompilationErrorMarkdownText. It
    // must read as a real error PAGE — a plain-language headline, the actual compiler
    // diagnostics, and a clear "correct the code" call to action — not a terse line.

    [Fact]
    public void InstanceOverlayPage_HasHeadline_RealDiagnostics_AndFixCallToAction()
    {
        var error = "Compilation failed for 'Acme/Widget'\n"
            + "CS0103 error (line 9): 'Chart' does not exist in the current context";

        var md = NodeTypeEnrichmentHelpers.BuildCompilationErrorMarkdownText(error, guidance: null);

        md.Should().Contain("⚠");
        md.Should().Contain("can't be displayed",
            "the page leads with a plain-language headline, not a raw one-liner");
        md.Should().Contain("compilation error",
            "the overlay contract (CompileErrorOverviewTest) keeps the word 'compilation'");
        md.Should().Contain("Compilation failed for 'Acme/Widget'",
            "the specific failure header is shown");
        md.Should().Contain("CS0103",
            "the actual compiler diagnostics are shown so the author can fix them");
        md.Should().Contain("```text",
            "diagnostics render in a code block, not inline prose");
        md.Should().Contain("Please correct the code",
            "the page ends with a clear call to action to fix the source");
    }

    [Fact]
    public void InstanceOverlayPage_SingleLineMessage_OmitsEmptyDiagnosticsFence_UsesCallerGuidance()
    {
        // A single-line message (no diagnostics body) must NOT render an empty ```text``` block,
        // and caller-supplied guidance is used verbatim when provided.
        var md = NodeTypeEnrichmentHelpers.BuildCompilationErrorMarkdownText(
            "Compilation failed", guidance: "Edit the source and recompile.");

        md.Should().NotContain("```text", "no diagnostics body → no empty code fence");
        md.Should().Contain("Please correct the code");
        md.Should().Contain("Edit the source and recompile.",
            "caller-supplied guidance is used when provided");
    }

    // ── A TIMEOUT is not a compile failure (#641) ──
    // Every overlay used to inherit "There was a compilation error… Please correct the
    // code" whenever the call site passed no guidance, so a 3s registration-lookup
    // timeout, a 60s settle timeout, an assembly the store couldn't hand over and an
    // ABI-stale DLL all told the author to fix source Roslyn had never rejected.
    // NodeTypeEnrichmentHelpers.OverlayCopy is now the SINGLE place that decides the
    // wording, and ImpliesCodeFix pins which causes may claim a code fix.

    [Fact]
    public void EveryOverlayCause_SaysCorrectTheCode_OnlyWhenACodeFixIsActuallyTheRemedy()
    {
        // Enumerated, not listed: a NEW cause added later is covered automatically and
        // cannot quietly inherit the genuine-error wording.
        foreach (var cause in Enum.GetValues<NodeTypeEnrichmentHelpers.OverlayCause>())
        {
            var (intro, callToAction, guidance) = NodeTypeEnrichmentHelpers.OverlayCopy(cause);
            var md = NodeTypeEnrichmentHelpers.BuildCompilationErrorMarkdownText(
                "NodeType 'Acme/Widget' build did not settle within 60s.\n"
                + "Instance 'Acme/widget-1' is rendering this fallback until the type's build settles.",
                guidance, intro, callToAction);

            md.Should().Contain("⚠", $"{cause}: every overlay keeps the emergency-page headline");
            md.Should().Contain("compilation",
                $"{cause}: the overlay contract CompileErrorOverviewTest asserts on the word 'compilation'");

            if (NodeTypeEnrichmentHelpers.ImpliesCodeFix(cause))
            {
                md.Should().Contain("Please correct the code",
                    $"{cause} genuinely IS the author's to fix");
            }
            else
            {
                md.Should().NotContain("Please correct the code",
                    $"{cause} is an availability problem — telling the author to edit working source is the #641 bug");
                md.Should().Contain("No code change is needed",
                    $"{cause} must say so explicitly, not merely omit the code-fix line");
            }
        }
    }

    [Fact]
    public void SettleTimeoutOverlay_ReadsAsRetry_AndStillNamesWhatTimedOut()
    {
        var (intro, callToAction, guidance) =
            NodeTypeEnrichmentHelpers.OverlayCopy(NodeTypeEnrichmentHelpers.OverlayCause.BuildNotSettled);

        var md = NodeTypeEnrichmentHelpers.BuildCompilationErrorMarkdownText(
            "NodeType 'Acme/Widget' build did not settle within 60s.\n"
            + "Instance 'Acme/widget-1' is rendering this fallback until the type's build settles.",
            guidance, intro, callToAction);

        md.Should().NotContain("Please correct the code");
        md.Should().Contain("No code change is needed");
        md.Should().Contain("Acme/Widget", "the page still names the type that didn't settle");
        md.Should().Contain("60s", "and the budget that elapsed — a bare 'timed out' is unactionable");
        md.Should().Contain("Recycle", "with a concrete way to retry");
    }

    [Fact]
    public void GenuineRoslynFailure_StillShowsTheCompilerOutput_AndAsksForACodeFix()
    {
        // The counterpart guarantee: distinguishing timeouts must not soften a REAL failure.
        var (intro, callToAction, guidance) =
            NodeTypeEnrichmentHelpers.OverlayCopy(NodeTypeEnrichmentHelpers.OverlayCause.CompileFailed);

        var md = NodeTypeEnrichmentHelpers.BuildCompilationErrorMarkdownText(
            "Compilation failed for 'Acme/Widget'\n"
            + "CS0103 error (line 9): 'Chart' does not exist in the current context",
            guidance, intro, callToAction);

        md.Should().Contain("Please correct the code");
        md.Should().Contain("CS0103", "the actual compiler diagnostics are still shown");
        md.Should().Contain("```text", "still in a code block, not prose");
        md.Should().NotContain("No code change is needed");
    }

    [Fact]
    public void InstanceOverlayPage_LinksTheCompileLog_WhenAnActivityPathIsKnown()
    {
        // 🚨 The broken INSTANCE page used to be the one surface with no route to the
        // diagnostics — only the NodeType's own Overview carried "View full compile log →".
        const string activityPath = "Acme/Widget/_Activity/compile-42";

        var md = NodeTypeEnrichmentHelpers.BuildCompilationErrorMarkdownText(
            "Compilation failed for 'Acme/Widget'\nCS0103 error (line 9): 'Chart' does not exist",
            guidance: null, intro: null, callToAction: null, activityPath: activityPath);

        md.Should().Contain($"](/{activityPath})",
            "the overlay links straight to the compile activity so the user reaches the full log");
        md.Should().Contain(NodeTypeEnrichmentHelpers.ViewCompileLogLabel);
    }

    [Fact]
    public void InstanceOverlayPage_TimeoutWithActivity_CarriesBothTheRetryCopyAndTheLogLink()
    {
        const string activityPath = "Acme/Widget/_Activity/compile-43";
        var (intro, callToAction, guidance) =
            NodeTypeEnrichmentHelpers.OverlayCopy(NodeTypeEnrichmentHelpers.OverlayCause.StateUndetermined);

        var md = NodeTypeEnrichmentHelpers.BuildCompilationErrorMarkdownText(
            "The compile state of 'Acme/Widget' could not be determined: it did not settle within 60s.",
            guidance, intro, callToAction, activityPath);

        md.Should().NotContain("Please correct the code");
        md.Should().Contain($"](/{activityPath})",
            "a timeout page needs the log MORE than a failure page — it's the only evidence of how far the build got");
    }

    [Fact]
    public void InstanceOverlayPage_OmitsTheCompileLogLink_WhenNoActivityWasEverRecorded()
    {
        // No phantom links: an activity path that was never written must not become a
        // dead route (a subscription to an inexistent address is the resubscribe storm).
        var md = NodeTypeEnrichmentHelpers.BuildCompilationErrorMarkdownText(
            "Compilation failed", guidance: null);

        md.Should().NotContain("](/");
        md.Should().NotContain(NodeTypeEnrichmentHelpers.ViewCompileLogLabel);
    }
}
