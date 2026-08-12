using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Docx;
using MeshWeaver.Markdown.Export.Pdf;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

public class RendererOutputTests : IDisposable
{
    private readonly IoPoolRegistry pools = new();

    private const string SampleMarkdown = """
        # Report

        This is an **executive** summary with a list:

        - one
        - two
        - three

        ## Details

        | Column A | Column B |
        | --- | --- |
        | a1 | b1 |
        | a2 | b2 |

        ```csharp
        var answer = 42;
        ```
        """;

    /// <summary>
    /// The whole document, printed by a real browser — cover, contents and body, each on its
    /// own page, with the running footer numbering them.
    ///
    /// <para><b>Never skipped</b>: with no browser the export must fail loudly rather than hand
    /// back something that lost its formatting, and that is a contract worth pinning too.</para>
    /// </summary>
    [Fact]
    public async Task Pdf_renderer_produces_a_paginated_pdf_or_fails_loudly()
    {
        var doc = new DocumentBuilder().Build(
            "Report",
            SampleMarkdown,
            new DocumentExportOptions { CoverPage = true, TableOfContents = true },
            BrandingOptions.Default with { Name = "Contoso", HeaderText = "Draft" });

        var browser = new HeadlessChromiumPdfRenderer(
            new PixelRenderingOptions { NoSandbox = OperatingSystem.IsLinux() }, pools);
        var renderer = new PdfDocumentRenderer(browser);

        if (await browser.Probe().FirstAsync().ToTask() is null)
        {
            var render = async () => await renderer.Render(doc).FirstAsync().ToTask();
            (await render.Should().ThrowAsync<PixelRendererUnavailableException>())
                .WithMessage("*headless Chromium*");
            return;
        }

        var pdf = await renderer.Render(doc).FirstAsync().ToTask();

        pdf.Take(4).Should().Equal((byte)'%', (byte)'P', (byte)'D', (byte)'F');

        using var pdfDoc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        pdfDoc.NumberOfPages.Should().BeGreaterThanOrEqualTo(3,
            "a cover, a contents page and at least one body page are three distinct pages");

        var cover = pdfDoc.GetPage(1).Text;
        cover.Should().Contain("Report").And.Contain("Contoso");
        cover.Should().NotContain("Draft", "the cover carries no running header");
        cover.Should().NotContain(" / ", "and no page number");

        var contents = pdfDoc.GetPage(2).Text;
        contents.Should().Contain("Contents").And.Contain("Details");
        contents.Should().Contain("Draft", "every page after the cover carries the running header");
        contents.Should().Contain("2 / " + pdfDoc.NumberOfPages);

        var body = string.Join("\n", pdfDoc.GetPages().Skip(2).Select(p => p.Text));
        body.Should().Contain("executive").And.Contain("Column A").And.Contain("var answer = 42;");
    }

    /// <summary>
    /// A4 by default, A4 landscape when the deck path asks for it — the page box comes from
    /// <c>@page size</c>, so this is what proves the browser honoured it.
    /// </summary>
    [Fact]
    public async Task Landscape_option_turns_the_page_box_on_its_side()
    {
        var browser = new HeadlessChromiumPdfRenderer(
            new PixelRenderingOptions { NoSandbox = OperatingSystem.IsLinux() }, pools);
        if (await browser.Probe().FirstAsync().ToTask() is null)
            return;

        var renderer = new PdfDocumentRenderer(browser);
        var options = new DocumentExportOptions { CoverPage = false, TableOfContents = false };

        var portrait = UglyToad.PdfPig.PdfDocument.Open(await renderer
            .Render(new DocumentBuilder().Build("P", "# P", options, BrandingOptions.Default))
            .FirstAsync().ToTask());
        var landscape = UglyToad.PdfPig.PdfDocument.Open(await renderer
            .Render(new DocumentBuilder().Build(
                "L", "# L", options with { Landscape = true }, BrandingOptions.Default))
            .FirstAsync().ToTask());

        using (portrait)
        using (landscape)
        {
            portrait.GetPage(1).Height.Should().BeGreaterThan(portrait.GetPage(1).Width);
            landscape.GetPage(1).Width.Should().BeGreaterThan(landscape.GetPage(1).Height);
        }
    }

    [Fact]
    public void Docx_renderer_produces_valid_docx_bytes()
    {
        var doc = new DocumentBuilder().Build(
            "Report",
            SampleMarkdown,
            new DocumentExportOptions { CoverPage = true, TableOfContents = true },
            BrandingOptions.Default);

        var docx = new DocxDocumentRenderer().Render(doc);

        docx.Should().NotBeEmpty();
        // .docx is a ZIP — magic bytes "PK"
        docx[0].Should().Be((byte)'P');
        docx[1].Should().Be((byte)'K');
    }

    public void Dispose() => pools.Dispose();
}
