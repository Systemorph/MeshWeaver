using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Docx;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

public class DocxTemplateRenderTests
{
    private static readonly string TemplatePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "template.docx");

    private const string SampleMarkdown = """
        # Template Report

        This is the rendered **body** that should replace the template's sample content.

        ## Section

        Some paragraph text.
        """;

    private static byte[] RenderWithTemplate()
    {
        var templateBytes = File.ReadAllBytes(TemplatePath);
        var branding = BrandingOptions.Default with { TemplateDocxBytes = templateBytes };

        var document = new DocumentBuilder().Build(
            "Template Report",
            SampleMarkdown,
            new DocumentExportOptions { CoverPage = false, TableOfContents = false, PageBreakBeforeH1 = false },
            branding);

        return new DocxDocumentRenderer().Render(document);
    }

    [Fact]
    public void Render_with_template_produces_valid_docx_zip()
    {
        var docx = RenderWithTemplate();

        docx.Should().NotBeEmpty();
        docx[0].Should().Be((byte)'P');
        docx[1].Should().Be((byte)'K');
    }

    [Fact]
    public void Render_with_template_preserves_header_and_footer_parts()
    {
        var docx = RenderWithTemplate();

        using var ms = new MemoryStream(docx);
        using var word = WordprocessingDocument.Open(ms, isEditable: false);

        word.MainDocumentPart!.HeaderParts.Should().NotBeEmpty("the template's header part must survive cloning");
        word.MainDocumentPart.FooterParts.Should().NotBeEmpty("the template's footer part must survive cloning");
    }

    [Fact]
    public void Render_with_template_preserves_embedded_media()
    {
        // The template's header references an embedded PNG logo; that image part
        // must still be in the output so Word can render the header.
        var docx = RenderWithTemplate();

        using var ms = new MemoryStream(docx);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        zip.Entries.Any(e =>
                e.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase) &&
                e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("the template's header logo must still be embedded");
    }

    [Fact]
    public void Render_with_template_keeps_section_properties_in_body()
    {
        var docx = RenderWithTemplate();

        using var ms = new MemoryStream(docx);
        using var word = WordprocessingDocument.Open(ms, isEditable: false);

        var document = word.MainDocumentPart?.Document;
        document.Should().NotBeNull();
        var body = document!.Body;
        body.Should().NotBeNull();
        body!.Elements<SectionProperties>().Should().ContainSingle(
            "SectionProperties bind the body to the preserved header/footer parts");
    }

    [Fact]
    public void Render_with_template_replaces_template_body_content_with_rendered_markdown()
    {
        var docx = RenderWithTemplate();

        using var ms = new MemoryStream(docx);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var docEntry = zip.Entries.Single(e =>
            e.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(docEntry.Open());
        var xml = reader.ReadToEnd();

        xml.Should().Contain("Template Report", "the rendered heading should be present");
        xml.Should().Contain("rendered", "rendered body text should be present");
    }

    [Fact]
    public void Render_without_template_still_produces_valid_docx()
    {
        // Regression guard: from-scratch path still works when TemplateDocxBytes is null.
        var document = new DocumentBuilder().Build(
            "Untemplated",
            SampleMarkdown,
            new DocumentExportOptions(),
            BrandingOptions.Default);

        var docx = new DocxDocumentRenderer().Render(document);

        docx.Should().NotBeEmpty();
        docx[0].Should().Be((byte)'P');
        docx[1].Should().Be((byte)'K');

        using var ms = new MemoryStream(docx);
        using var word = WordprocessingDocument.Open(ms, isEditable: false);
        word.MainDocumentPart!.HeaderParts.Should().NotBeEmpty();
    }
}
