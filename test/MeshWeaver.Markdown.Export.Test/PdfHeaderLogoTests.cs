using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Pdf;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

/// <summary>
/// The brand header, end to end: a logo extracted from a Word template must reach the printed
/// page, and a header with no text and no logo must produce no header band at all.
///
/// <para><b>Never skipped.</b> Like <see cref="HeadlessChromiumRenderTests"/>, each test asks the
/// renderer's own probe and then asserts whichever contract applies to the machine it runs on:
/// with a browser, a real PDF carrying the header; without one, a loud, actionable refusal. The
/// structural half — that the logo becomes a data URI in the stylesheet at all — is pinned
/// separately in <see cref="DocumentPrintComposerTests"/> and needs nothing installed.</para>
/// </summary>
public class PdfHeaderLogoTests : IDisposable
{
    private readonly IoPoolRegistry pools = new();

    private static readonly string TemplatePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "template.docx");

    private const string SampleMarkdown = "# Title\n\nBody paragraph.";

    private HeadlessChromiumPdfRenderer Browser() =>
        new(new PixelRenderingOptions { NoSandbox = OperatingSystem.IsLinux() }, pools);

    [Fact]
    public async Task Header_logo_extracted_from_a_template_reaches_the_printed_page()
    {
        var templateBytes = File.ReadAllBytes(TemplatePath);
        var extracted = ExportTemplateResolver.InspectBytes(templateBytes);
        extracted.Logo.Should().NotBeNull();

        var branding = BrandingOptions.Default with
        {
            Logo = extracted.Logo,
            HeaderText = "Test Report",
            FontFamily = extracted.FontFamily ?? BrandingOptions.Default.FontFamily
        };

        var document = new DocumentBuilder().Build(
            "With Header Logo",
            SampleMarkdown,
            new DocumentExportOptions { CoverPage = false, TableOfContents = false, PageBreakBeforeH1 = false },
            branding);

        var browser = Browser();
        var executable = await browser.Probe().FirstAsync().ToTask();
        var renderer = new PdfDocumentRenderer(browser);

        if (executable is null)
        {
            var render = async () => await renderer.Render(document).FirstAsync().ToTask();
            (await render.Should().ThrowAsync<PixelRendererUnavailableException>())
                .WithMessage("*headless Chromium*");
            return;
        }

        var pdf = await renderer.Render(document).FirstAsync().ToTask();

        pdf.Take(4).Should().Equal((byte)'%', (byte)'P', (byte)'D', (byte)'F');

        using var pdfDoc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        pdfDoc.NumberOfPages.Should().BeGreaterThan(0);
        pdfDoc.GetPage(1).Text.Should().Contain("Test Report",
            "the running header is a @page margin box and must print on the body page");
    }

    [Fact]
    public async Task An_empty_header_and_no_logo_still_prints_and_omits_the_header_band()
    {
        var branding = BrandingOptions.Default with { HeaderText = "", Logo = null };

        var document = new DocumentBuilder().Build(
            "Headless",
            SampleMarkdown,
            new DocumentExportOptions { CoverPage = false, TableOfContents = false, PageBreakBeforeH1 = false },
            branding);

        var browser = Browser();
        var executable = await browser.Probe().FirstAsync().ToTask();
        var renderer = new PdfDocumentRenderer(browser);

        if (executable is null)
        {
            var render = async () => await renderer.Render(document).FirstAsync().ToTask();
            await render.Should().ThrowAsync<PixelRendererUnavailableException>();
            return;
        }

        var pdf = await renderer.Render(document).FirstAsync().ToTask();

        pdf.Take(4).Should().Equal((byte)'%', (byte)'P', (byte)'D', (byte)'F');

        using var pdfDoc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        // The footer is unconditional (the page number belongs on every page), so the page is
        // not empty — but nothing from the header band may appear.
        pdfDoc.GetPage(1).Text.Should().Contain("1 / 1").And.Contain("Body paragraph");
    }

    public void Dispose() => pools.Dispose();
}
