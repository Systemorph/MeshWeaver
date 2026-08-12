using Markdig;
using MeshWeaver.Markdown;

namespace MeshWeaver.Markdown.Export.Ast;

/// <summary>
/// The ONE Markdig pipeline the content-faithful export parses with — used both to FIND embedded
/// layout areas ahead of rendering and to WALK the document while rendering.
///
/// <para>🚨 This type exists because of a real, silent defect. <see cref="DocumentBuilder"/> used to
/// build its own pipeline inline (<c>UseAdvancedExtensions().UsePageBreaks()</c>) which omitted
/// <see cref="LayoutAreaMarkdownExtension"/>. An <c>@@("area:…")</c> embed was therefore never
/// recognised as a block at all: it fell through to a paragraph and every PDF and DOCX printed the
/// embed's SOURCE TEXT where the author expected a view. Nothing failed, nothing logged, and no
/// test covered it — the export had been quietly wrong for every document with an embed.</para>
///
/// <para>Two pipelines were the root cause, so there is now one, and resolution and rendering share
/// it by construction: whatever the resolver can find, the builder can render, because they parse
/// with the same object. Adding an extension here adds it to both halves at once.</para>
///
/// <para>The export pipeline is deliberately the framework's layout-area extension plus the
/// export's OWN page-break extension, rather than the whole portal pipeline
/// (<see cref="MarkdownExtensions.CreateMarkdownPipeline"/>). The portal pipeline also enables
/// mathematics, image-path rewriting and executable code blocks, whose block types the document
/// model has no representation for — adopting them wholesale would silently DROP content that
/// currently renders as text. Widening it is a deliberate, test-covered step per extension, not a
/// side effect of this fix.</para>
/// </summary>
public static class ExportMarkdownPipeline
{
    /// <summary>
    /// Builds the export pipeline for a document rooted at <paramref name="currentNodePath"/>.
    ///
    /// <para>The node path is what lets a RELATIVE embed resolve: <c>@@("area:Foo")</c> written in a
    /// node means "this node's Foo area", and the parser needs the node's own path to turn that
    /// into an address. Passing null still parses the syntax — only relative resolution is lost.</para>
    /// </summary>
    public static MarkdownPipeline For(string? currentNodePath) =>
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePageBreaks()
            .Use(new LayoutAreaMarkdownExtension(currentNodePath))
            .Build();
}
