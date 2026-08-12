using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Html;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins that <b>an exported document contains its embedded layout areas</b> — the defect that made
/// every PDF and DOCX quietly wrong for any document using <c>@@(…)</c>.
///
/// <para>There were two distinct failures, and this class pins both because they failed for
/// opposite reasons:</para>
/// <list type="number">
/// <item><description><b>Content-faithful PDF/DOCX printed the embed's SOURCE TEXT.</b>
/// <c>DocumentBuilder</c> parsed with a private Markdig pipeline that omitted
/// <c>LayoutAreaMarkdownExtension</c>, so <c>@@("…/area/OgCard/…")</c> was never recognised as a
/// block and fell through to a paragraph.</description></item>
/// <item><description><b>The pixel path printed a BLANK.</b> It parses with the framework pipeline,
/// so it correctly emitted the empty <c>&lt;div class='layout-area'&gt;</c> anchor a browser session
/// would fill — but the print document is loaded from <c>file://</c> under a CSP of
/// <c>default-src 'none'</c> with the resolver pointed at nothing, so the browser could never fill
/// it.</description></item>
/// </list>
///
/// <para>Both were silent: no error, no log, no failing test. So every assertion here is made on
/// the BYTES a user would actually download, and each one checks the literal source is gone AND
/// the area's real content is present — either alone would pass while the other half stayed
/// broken.</para>
/// </summary>
public class DocumentExportLayoutAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Single-token sentinels on purpose. PDF text extraction reconstructs words from glyph
    // positions and does not reliably preserve inter-word spacing, so a multi-word phrase can
    // come back joined or split. HeadlessChromiumRenderTests uses the same trick.
    private const string CardTitleToken = "QUANTUMLEDGER";
    private const string CardDescriptionToken = "RECONCILIATIONSENTINEL";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMarkdownExport();

    [Fact(Timeout = 180000)]
    public async Task PdfExport_RendersEmbeddedLayoutArea_NotItsSourceText()
    {
        var (space, doc) = await SeedDocumentWithEmbed();

        var rendered = await Export(doc, ExportFormat.Pdf);
        rendered.MimeType.Should().Be("application/pdf");

        var text = ReadPdfText(rendered.Content);

        // The area is REALLY there — resolved off the target node's live stream, server-side.
        text.Should().Contain(CardTitleToken,
            "the embedded OgCard area must render its card into the PDF");
        text.Should().Contain(CardDescriptionToken,
            "the card's description comes from the target node's live stream");

        // …and the embed's SOURCE is not. This is the exact regression: before the fix the PDF
        // contained the literal `@@("…")` text and no card at all.
        text.Should().NotContain("@@(",
            "the embed must be rendered, never printed as its own markdown source");
        text.Should().NotContain("layout-area",
            "the placeholder anchor is an implementation detail and must never reach a document");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    [Fact(Timeout = 180000)]
    public async Task DocxExport_RendersEmbeddedLayoutArea_AsRealWordContent()
    {
        var (space, doc) = await SeedDocumentWithEmbed();

        var rendered = await Export(doc, ExportFormat.Docx);
        rendered.MimeType.Should()
            .Be("application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        var xml = ReadDocxBody(rendered.Content);
        var text = StripTags(xml);

        text.Should().Contain(CardTitleToken,
            "the embedded area must render into the Word document");
        text.Should().Contain(CardDescriptionToken);
        text.Should().NotContain("@@(",
            "the embed must be rendered, never written as its own markdown source");

        // A card grid is laid out as a real Word TABLE, not as a run of text pretending to be one:
        // `w:tbl` is what makes it survive editing, repagination and a reader's own styles.
        xml.Should().Contain("<w:tbl>",
            "the resolved card grid must land as a native Word table");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    /// <summary>
    /// An area that cannot be resolved must degrade to something a reader can SEE.
    ///
    /// <para>The tempting behaviours are both wrong: leaving the empty anchor prints nothing, and
    /// deleting it prints nothing — in either case the document looks complete while silently
    /// missing a section its author placed, and neither the author nor the recipient can tell. A
    /// raw exception dump is the opposite failure. So the contract is a short, localized notice
    /// that names the area and says where to see it.</para>
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task UnresolvableArea_BecomesVisibleNotice_NeverSilentBlankOrErrorDump()
    {
        var space = $"Space{Guid.NewGuid():N}"[..16];
        await SeedSpace(space);

        var doc = $"{space}/BrokenEmbed";
        // An area name nothing in the mesh serves. This is the permission-denied / unknown-area /
        // fetch-failure class, all of which converge on "the stream yields nothing".
        var markdown = "# Report\n\nBefore the embed.\n\n"
                       + $"@@(\"{doc}/area/NoSuchAreaAnywhere\")\n\nAfter the embed.\n";
        await CreateMarkdownNode(doc, "Broken Embed", markdown);

        var rendered = await Export(doc, ExportFormat.Pdf);
        var text = ReadPdfText(rendered.Content);

        // Visible, and it names the area so the author can find what broke.
        text.Should().Contain("NoSuchAreaAnywhere",
            "the notice must name the area that could not be rendered");

        // Honest, not noisy — no stack trace, no exception type, no raw source.
        text.Should().NotContain("@@(",
            "a failed embed must not fall back to printing its own source");
        text.Should().NotContain("Exception",
            "a document a user is about to send must never carry an error dump");
        text.Should().NotContain("   at ",
            "no stack frames in a rendered document");

        // The surrounding document still rendered — one bad embed cannot fail the whole export.
        text.Should().Contain("Before the embed");
        text.Should().Contain("After the embed");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    /// <summary>
    /// The pixel/deck path, asserted WITHOUT a browser.
    ///
    /// <para>The browser is deliberately not shipped in the portal image, so a test that needed one
    /// would silently cover nothing on most machines. The defect is fully observable one step
    /// earlier: the composed print HTML either still carries the empty anchor (blank page) or
    /// carries the area's real markup. That is the assertion, and it holds on every machine.</para>
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task PixelPrintHtml_ResolvesEmbeddedArea_SoTheBrowserNeverPrintsABlank()
    {
        var (space, doc) = await SeedDocumentWithEmbed();

        var slide = new SlideContent
        {
            Content = $"# Deck slide\n\n@@(\"{doc}/area/OgCard/{space}/Target\")\n"
        };
        var html = SlidePrintComposer.Compose(
            "Pixel Deck", [new PrintSlide(slide, doc, doc)]);

        // Precondition — this is what the browser would have received before the fix, and why it
        // printed a blank: an empty anchor, inside a document whose CSP forbids fetching anything.
        html.Should().Contain(LayoutAreaMarkdownRenderer.LayoutArea,
            "the framework pipeline emits the anchor; if this stops being true the test below "
            + "would pass for the wrong reason");

        var document = new HtmlAgilityPack.HtmlDocument();
        document.LoadHtml(html);
        var result = await LayoutAreaResolver
            .Resolve(document, Mesh, new DocumentHtmlOptions("https://portal.example.com"))
            .FirstAsync()
            .ToTask();

        result.Resolved.Should().Be(1, "the slide embeds exactly one resolvable area");
        result.Unresolved.Should().Be(0);

        var resolvedHtml = document.DocumentNode.OuterHtml;
        resolvedHtml.Should().Contain(CardTitleToken,
            "the area's real content must be in the HTML the browser prints");
        resolvedHtml.Should().NotContain("data-area=",
            "no unresolved anchor may survive into the print document");

        // The print document's isolation must survive the rewrite: the CSP is what stops a slide
        // from making the server's browser fetch anything, and it only works if it is still there.
        resolvedHtml.Should().Contain("Content-Security-Policy",
            "resolving areas must not strip the print document's CSP");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private async Task<(string Space, string Doc)> SeedDocumentWithEmbed()
    {
        var space = $"Space{Guid.NewGuid():N}"[..16];
        await SeedSpace(space);

        // The card TARGET is a real mesh node, so the OgCard area resolves off its node stream
        // with no outbound HTTP at all.
        var target = $"{space}/Target";
        await NodeFactory.CreateNode(MeshNode.FromPath(target) with
        {
            Name = CardTitleToken,
            Description = CardDescriptionToken,
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent { Content = "# Target" }
        }).Should().Emit();

        var doc = $"{space}/Report";
        var markdown = "# Quarterly Report\n\nSome prose before the embed.\n\n"
                       + $"@@(\"{doc}/area/OgCard/{target}\")\n";
        await CreateMarkdownNode(doc, "Quarterly Report", markdown);
        return (space, doc);
    }

    private async Task SeedSpace(string space) =>
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Export Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Emit();

    private async Task CreateMarkdownNode(string path, string name, string markdown) =>
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent { Content = markdown }
        }).Should().Emit();

    /// <summary>
    /// Runs the real export pipeline end to end — request → script template → ActivityLog — and
    /// returns the rendered bytes. Deliberately not a direct renderer call: the templates are
    /// <c>.csx</c> compiled at RUNTIME, so a break in them is invisible to <c>dotnet build</c> and
    /// only a test that actually executes them can catch it.
    /// </summary>
    private async Task<RenderedDocument> Export(string sourcePath, ExportFormat format)
    {
        var request = new ExportDocumentRequest(sourcePath, new DocumentExportOptions
        {
            Format = format,
            CoverPage = false,
            TableOfContents = false,
            BaseUrl = "https://portal.example.com"
        });

        var dispatch = await Mesh
            .Observe<ExportDocumentResponse>(request, o => o.WithTarget(new Address(sourcePath)))
            .Should().Within(30.Seconds()).Emit();
        dispatch.Message.Error.Should().BeNullOrEmpty("the export should start cleanly");

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var terminal = await workspace
            .GetMeshNodeStream(dispatch.Message.ActivityPath)
            .Select(node => node?.Content as ActivityLog)
            .Should().Within(2.Minutes())
            .Match(log => log is not null && log.Status != ActivityStatus.Running);

        terminal!.Status.Should().Be(ActivityStatus.Succeeded,
            because: "the export script must run clean. Messages:\n  "
                     + string.Join("\n  ", terminal.Messages.Select(m => $"[{m.LogLevel}] {m.Message}")));

        var rendered = terminal.ReturnValue!.Value.Deserialize<RenderedDocument>(
            Mesh.JsonSerializerOptions);
        rendered.Should().NotBeNull();
        rendered!.Content.Should().NotBeNull().And.NotBeEmpty();
        return rendered;
    }

    private static string ReadPdfText(byte[] bytes)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        return string.Join("\n", pdf.GetPages().Select(p => p.Text));
    }

    private static string ReadDocxBody(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry("word/document.xml");
        entry.Should().NotBeNull("a .docx always carries word/document.xml");
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string StripTags(string xml) =>
        Regex.Replace(xml, "<[^>]+>", string.Empty);
}
