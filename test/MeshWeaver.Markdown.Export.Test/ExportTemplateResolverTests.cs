using System;
using System.IO;
using FluentAssertions;
using MeshWeaver.Markdown.Export.Branding;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

public class ExportTemplateResolverTests
{
    private static readonly string TemplatePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "template.docx");

    [Fact]
    public void InspectBytes_extracts_major_font_from_theme()
    {
        var bytes = File.ReadAllBytes(TemplatePath);

        var template = ExportTemplateResolver.InspectBytes(bytes);

        template.FontFamily.Should().Be("Overpass ExtraBold");
    }

    [Fact]
    public void InspectBytes_extracts_first_raster_image_as_png_logo()
    {
        var bytes = File.ReadAllBytes(TemplatePath);

        var template = ExportTemplateResolver.InspectBytes(bytes);

        template.Logo.Should().NotBeNull();
        template.Logo!.MimeType.Should().Be("image/png");
        template.Logo.Bytes.Length.Should().BeGreaterThan(1000);

        // PNG magic bytes: 0x89 'P' 'N' 'G'
        template.Logo.Bytes[0].Should().Be(0x89);
        template.Logo.Bytes[1].Should().Be((byte)'P');
        template.Logo.Bytes[2].Should().Be((byte)'N');
        template.Logo.Bytes[3].Should().Be((byte)'G');
    }

    [Fact]
    public void InspectBytes_preserves_original_bytes_in_result()
    {
        var bytes = File.ReadAllBytes(TemplatePath);

        var template = ExportTemplateResolver.InspectBytes(bytes);

        template.DocxBytes.Should().BeSameAs(bytes);
    }

    [Fact]
    public void InspectBytes_passes_through_and_logs_when_bytes_are_not_a_zip()
    {
        var garbage = new byte[] { 0x00, 0x01, 0x02, 0x03 };

        var template = ExportTemplateResolver.InspectBytes(garbage);

        template.DocxBytes.Should().BeSameAs(garbage);
        template.Logo.Should().BeNull();
        template.FontFamily.Should().BeNull();
    }
}
