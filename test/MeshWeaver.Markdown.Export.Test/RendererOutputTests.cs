using System;
using System.Globalization;
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
        var renderer = new PdfDocumentRenderer(browser, pools);

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
        // #1309: the contents list names the page, not just the link. Both sections are on the
        // one body page here, so both entries read 3 — and 3 is where the body actually starts.
        // Whitespace is stripped so the assertion does not depend on whether the extractor puts
        // a gap between an entry's title and its number.
        Packed(contents).Should().Contain("Report3").And.Contain("Details3");

        var body = string.Join("\n", pdfDoc.GetPages().Skip(2).Select(p => p.Text));
        body.Should().Contain("executive").And.Contain("Column A").And.Contain("var answer = 42;");
    }

    /// <summary>
    /// #1309: every page number the contents list prints is the page that section actually
    /// starts on — across page boundaries, for the first entry and for the last.
    ///
    /// <para><b>The oracle is independent of the mechanism.</b> The renderer works out the numbers
    /// from the printed PDF's link annotations; this test works them out from the printed PDF's
    /// TEXT — the page whose glyphs contain that section's (unique) heading. So an error in the
    /// annotation reading cannot also produce the expectation, which is the failure mode that makes
    /// "assert the number 12 appears somewhere" worthless. All three are then required to agree:
    /// what the contents list PRINTS, where the heading is DRAWN, and where the link JUMPS.</para>
    ///
    /// <para>The document is built so that this is a real test of pagination and not of a one-page
    /// body: one section is long enough to run over several pages, so the numbers are neither
    /// consecutive nor all equal, and a section boundary falls mid-page.</para>
    /// </summary>
    [Fact]
    public async Task Contents_page_numbers_name_the_page_each_section_actually_starts_on()
    {
        // Distinctive one-word headings: each must appear on exactly one BODY page, so the text
        // oracle is unambiguous, and none may collide with the filler.
        string[] headings = ["Alphaville", "Bravissimo", "Charliehorse", "Deltawing", "Echoplex"];
        int[] paragraphs = [6, 22, 3, 14, 2];

        var markdown = string.Join("\n\n", headings.Select((h, i) =>
            $"# {h}\n\n" + string.Join("\n\n", Enumerable.Range(0, paragraphs[i])
                .Select(p => $"Filler {p} for the section. " + string.Concat(
                    Enumerable.Repeat("lorem ipsum dolor sit amet ", 12))))));

        var doc = new DocumentBuilder().Build(
            "Boundary Report",
            markdown,
            new DocumentExportOptions { CoverPage = true, TableOfContents = true },
            BrandingOptions.Default with { Name = "Contoso", HeaderText = "Draft" });

        var browser = new HeadlessChromiumPdfRenderer(
            new PixelRenderingOptions { NoSandbox = OperatingSystem.IsLinux() }, pools);
        var renderer = new PdfDocumentRenderer(browser, pools);

        if (await browser.Probe().FirstAsync().ToTask() is null)
        {
            var render = async () => await renderer.Render(doc).FirstAsync().ToTask();
            (await render.Should().ThrowAsync<PixelRendererUnavailableException>())
                .WithMessage("*headless Chromium*");
            return;
        }

        var pdf = await renderer.Render(doc).FirstAsync().ToTask();
        using var pdfDoc = UglyToad.PdfPig.PdfDocument.Open(pdf);

        // Page 1 cover, page 2 contents, body from page 3 — asserted rather than assumed, because
        // every expectation below is stated in terms of it.
        pdfDoc.GetPage(2).Text.Should().Contain("Contents");
        pdfDoc.GetPage(3).Text.Should().NotContain("Contents", "the contents list fits one page");
        pdfDoc.NumberOfPages.Should().BeGreaterThan(headings.Length + 2,
            "the body must span more pages than it has sections, or nothing here crosses a boundary");

        // The oracle: the first body page whose glyphs carry this heading.
        var landsOn = headings.Select(h => Enumerable.Range(3, pdfDoc.NumberOfPages - 2)
            .First(p => pdfDoc.GetPage(p).Text.Contains(h, StringComparison.Ordinal))).ToArray();

        landsOn.Should().OnlyHaveUniqueItems("each heading starts exactly one section")
            .And.BeInAscendingOrder();
        landsOn[0].Should().Be(3, "the first section starts on the first body page");
        landsOn.Should().Contain(p => p > 3 + 1,
            "a section that begins two or more pages in is what proves a boundary was crossed");

        // 1. What the contents list PRINTS.
        var contents = Packed(pdfDoc.GetPage(2).Text);
        for (var i = 0; i < headings.Length; i++)
            contents.Should().Contain(headings[i] + landsOn[i].ToString(CultureInfo.InvariantCulture),
                $"the contents entry for '{headings[i]}' must print the page it starts on");

        // 2. Where the link JUMPS — the same reading the renderer used, re-run on the published PDF.
        var lookup = TocPageNumbers.Resolve(pdf, headings.Length);
        lookup.Resolved.Should().BeTrue(lookup.Refusal ?? "resolved");
        lookup.Pages.Should().Equal(landsOn);

        // And the read-back REFUSES rather than improvises when the document it is given does not
        // match the count it was told to expect — the guard that turns a mis-identification into
        // a contents list with no numbers instead of one with wrong ones.
        var tooMany = TocPageNumbers.Resolve(pdf, headings.Length + 1);
        tooMany.Resolved.Should().BeFalse();
        tooMany.Refusal.Should().Contain("internal links");
        TocPageNumbers.Resolve(pdf, 0).Resolved.Should().BeFalse();
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

        var renderer = new PdfDocumentRenderer(browser, pools);
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

    /// <summary>
    /// Extracted page text with every space removed, so an assertion pairing a contents entry
    /// with its page number ("Details" immediately followed by "3") does not depend on whether
    /// the PDF text extractor chooses to put a gap across the column that separates them.
    /// </summary>
    private static string Packed(string pageText) =>
        new(pageText.Where(c => !char.IsWhiteSpace(c)).ToArray());

    public void Dispose() => pools.Dispose();
}
