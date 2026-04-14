using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Pdf;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

public class PdfHeaderLogoTests
{
    private static readonly string TemplatePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "template.docx");

    private const string SampleMarkdown = "# Title\n\nBody paragraph.";

    [Fact]
    public void Pdf_renderer_embeds_header_logo_extracted_from_template()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

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

        var pdf = new PdfDocumentRenderer().Render(document);

        pdf.Should().NotBeEmpty();
        pdf.Take(4).Should().Equal((byte)'%', (byte)'P', (byte)'D', (byte)'F');

        using var ms = new MemoryStream(pdf);
        using var pdfDoc = UglyToad.PdfPig.PdfDocument.Open(ms);
        pdfDoc.NumberOfPages.Should().BeGreaterThan(0);

        // The header text should land on the first content page.
        var firstPageText = pdfDoc.GetPage(1).Text;
        firstPageText.Should().Contain("Test Report");
    }

    [Fact]
    public void Pdf_renderer_header_without_logo_still_renders_without_throwing()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        var branding = BrandingOptions.Default with
        {
            HeaderText = "No Logo Here",
            Logo = null
        };

        var document = new DocumentBuilder().Build(
            "Text Only Header",
            SampleMarkdown,
            new DocumentExportOptions { CoverPage = false, TableOfContents = false, PageBreakBeforeH1 = false },
            branding);

        var pdf = new PdfDocumentRenderer().Render(document);

        pdf.Should().NotBeEmpty();
        pdf.Take(4).Should().Equal((byte)'%', (byte)'P', (byte)'D', (byte)'F');
    }

    [Fact]
    public void Pdf_renderer_empty_header_and_no_logo_skips_header_band()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        var branding = BrandingOptions.Default with
        {
            HeaderText = "",
            Logo = null
        };

        var document = new DocumentBuilder().Build(
            "Headless",
            SampleMarkdown,
            new DocumentExportOptions { CoverPage = false, TableOfContents = false, PageBreakBeforeH1 = false },
            branding);

        var pdf = new PdfDocumentRenderer().Render(document);

        pdf.Should().NotBeEmpty();
        pdf.Take(4).Should().Equal((byte)'%', (byte)'P', (byte)'D', (byte)'F');
    }
}
