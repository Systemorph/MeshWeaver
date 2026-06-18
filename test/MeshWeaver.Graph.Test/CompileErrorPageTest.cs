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
}
