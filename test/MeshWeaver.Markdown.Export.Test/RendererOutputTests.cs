using System.IO;
using System.Linq;
using FluentAssertions;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Docx;
using MeshWeaver.Markdown.Export.Pdf;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

public class RendererOutputTests
{
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

    [Fact]
    public void Pdf_renderer_produces_valid_pdf_bytes()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        var doc = new DocumentBuilder().Build(
            "Report",
            SampleMarkdown,
            new DocumentExportOptions { CoverPage = true, TableOfContents = true },
            BrandingOptions.Default);

        var pdf = new PdfDocumentRenderer().Render(doc);

        pdf.Should().NotBeEmpty();
        pdf.Take(4).Should().Equal((byte)'%', (byte)'P', (byte)'D', (byte)'F');

        // Sanity check: PdfPig can open it and read some text.
        using var ms = new MemoryStream(pdf);
        using var pdfDoc = UglyToad.PdfPig.PdfDocument.Open(ms);
        pdfDoc.NumberOfPages.Should().BeGreaterThan(0);
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
}
