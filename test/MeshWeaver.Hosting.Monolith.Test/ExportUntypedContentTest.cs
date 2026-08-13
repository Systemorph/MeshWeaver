using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using UglyToad.PdfPig;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// #1374 — a document whose body was stored as plain JSON must still EXPORT its body.
///
/// <para>The three export templates extracted the body with <c>node.Content is MarkdownContent</c>.
/// That test is true only when the value already happens to be that CLR type. A body written as
/// bare JSON — an import, an MCP <c>create</c>/<c>patch</c> carrying a raw content object, a
/// document older than its content type — has no <c>$type</c> for the polymorphic converter to
/// resolve, so it stays a raw <see cref="JsonElement"/>: not degraded in transit but STORED that
/// way, and therefore still a <c>JsonElement</c> when read from the node's own per-node hub, the
/// very hub that declares <c>WithContentType&lt;MarkdownContent&gt;()</c>. The test was false, the
/// extractor returned <c>""</c>, and the export produced a cover page, a contents list and no
/// body — no exception, nothing logged, nothing to grep, and it reads to the author as their own
/// empty document rather than as a platform bug.</para>
///
/// <para>Asserted on the RENDERED ARTEFACT, not the document model: the bug is that a real reader
/// opens a real file and finds it empty, and only the file can say that did not happen. Both
/// formats are covered because both templates carried the same extractor — PDF through the
/// headless-browser print, DOCX through the OpenXml writer.</para>
/// </summary>
public class ExportUntypedContentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Single contiguous words: PDF text extraction keeps them intact.</summary>
    private const string BodyToken = "ZANZIBARWIDGET";
    private const string ChildToken = "QUOKKAENGINE";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMarkdownExport();

    [Fact(Timeout = 180000)]
    public async Task Pdf_export_of_a_document_stored_as_untyped_json_still_carries_its_body()
    {
        var doc = await SeedDocumentStoredAsUntypedJson();

        var rendered = await Export(doc, ExportFormat.Pdf);

        rendered.MimeType.Should().Be("application/pdf");
        using var pdf = PdfDocument.Open(rendered.Content);
        var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        Output.WriteLine($"PDF pages={pdf.NumberOfPages}\n{text}");

        text.Should().Contain(BodyToken,
            "the document's own body must reach the page — its absence is the whole of #1374");
        text.Should().Contain(ChildToken,
            "a descendant chapter stored the same way must not be dropped either");
    }

    [Fact(Timeout = 180000)]
    public async Task Docx_export_of_a_document_stored_as_untyped_json_still_carries_its_body()
    {
        var doc = await SeedDocumentStoredAsUntypedJson();

        var rendered = await Export(doc, ExportFormat.Docx);

        rendered.MimeType.Should().Be(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        // Word stores the body as XML inside the package; the token has to be IN it.
        using var package = System.IO.Packaging.Package.Open(
            new System.IO.MemoryStream(rendered.Content), System.IO.FileMode.Open, System.IO.FileAccess.Read);
        var part = package.GetPart(new Uri("/word/document.xml", UriKind.Relative));
        using var reader = new System.IO.StreamReader(part.GetStream(), Encoding.UTF8);
        var xml = await reader.ReadToEndAsync();

        xml.Should().Contain(BodyToken, "the DOCX body must carry the document's own text");
        xml.Should().Contain(ChildToken, "and its descendant chapter's text");
    }

    /// <summary>
    /// Seeds a document plus one child, both with content written as bare JSON — no <c>$type</c>,
    /// which is what makes the content unresolvable by every registry there is. The child covers
    /// the <c>IncludeChildren</c> path, where an empty body is silently SKIPPED rather than
    /// rendered blank, so a lost chapter leaves no trace at all.
    /// </summary>
    private async Task<string> SeedDocumentStoredAsUntypedJson()
    {
        var space = $"Space{Guid.NewGuid():N}"[..16];
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Untyped Export Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Emit();

        var doc = $"{space}/Report";
        await NodeFactory.CreateNode(MeshNode.FromPath(doc) with
        {
            Name = "Report",
            NodeType = MarkdownNodeType.NodeType,
            Content = JsonSerializer.SerializeToElement(
                new { content = $"# Report\n\nThe body mentions {BodyToken} exactly once." })
        }).Should().Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath($"{doc}/Appendix") with
        {
            Name = "Appendix",
            NodeType = MarkdownNodeType.NodeType,
            Content = JsonSerializer.SerializeToElement(
                new { content = $"# Appendix\n\nThe appendix mentions {ChildToken}." })
        }).Should().Emit();

        // Precondition: the content really is untyped where the export reads it. Without this the
        // test could pass on a re-typed value and prove nothing about the defect.
        var read = await Mesh.GetMeshNode(doc, TimeSpan.FromSeconds(20)).Should().Within(30.Seconds()).Emit();
        read!.Content.Should().BeOfType<JsonElement>(
            "the stored shape carries no $type, so no registry — not even the node's own hub — can type it");

        return doc;
    }

    private async Task<RenderedDocument> Export(string doc, ExportFormat format)
    {
        var dispatch = await Mesh
            .Observe<ExportDocumentResponse>(
                new ExportDocumentRequest(doc, new DocumentExportOptions
                {
                    Format = format,
                    IncludeChildren = true,
                    CoverPage = false,
                    TableOfContents = false
                }),
                o => o.WithTarget(new Address(doc)))
            .Should().Within(30.Seconds()).Emit();

        dispatch.Message.Error.Should().BeNullOrEmpty("the export should start successfully");

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var terminal = await workspace
            .GetMeshNodeStream(dispatch.Message.ActivityPath)
            .Select(node => node?.Content as ActivityLog)
            .Should().Within(2.Minutes())
            .Match(log => log is not null && log.Status != ActivityStatus.Running);

        terminal!.Status.Should().Be(ActivityStatus.Succeeded,
            because: "the export should render without errors. Messages:\n  "
                     + string.Join("\n  ", terminal.Messages.Select(m => $"[{m.LogLevel}] {m.Message}")));

        var rendered = terminal.ReturnValue!.Value.Deserialize<RenderedDocument>(Mesh.JsonSerializerOptions);
        rendered.Should().NotBeNull();
        rendered!.Content.Should().NotBeNull().And.NotBeEmpty();
        return rendered;
    }
}
