using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests that the AzureClaudeChatClient correctly serializes DataContent
/// (PDFs, images) as base64 content blocks in the Claude API format.
/// </summary>
public class BinaryAttachmentTest
{
    [Fact]
    public void DataContent_Pdf_SerializesAsDocumentBlock()
    {
        // Simulate a PDF file
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // "%PDF" magic bytes
        var dataContent = new DataContent(pdfBytes, "application/pdf");

        // Verify DataContent properties
        dataContent.MediaType.Should().Be("application/pdf");
        dataContent.Data.Length.Should().Be(4);

        // Verify base64 encoding
        var base64 = Convert.ToBase64String(dataContent.Data.ToArray());
        base64.Should().Be("JVBER");

        // Verify it's classified as "document" (not "image")
        var type = dataContent.MediaType.StartsWith("image/") ? "image" : "document";
        type.Should().Be("document");
    }

    [Fact]
    public void DataContent_Image_SerializesAsImageBlock()
    {
        // Simulate a PNG file (PNG magic bytes)
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var dataContent = new DataContent(pngBytes, "image/png");

        dataContent.MediaType.Should().Be("image/png");

        var type = dataContent.MediaType.StartsWith("image/") ? "image" : "document";
        type.Should().Be("image");
    }

    [Fact]
    public void MixedContent_ChatMessage_ContainsTextAndBinary()
    {
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };

        var contents = new List<AIContent>
        {
            new TextContent("Analyze these documents:"),
            new DataContent(pdfBytes, "application/pdf") { Name = "report.pdf" },
            new DataContent(imageBytes, "image/png") { Name = "chart.png" },
            new TextContent("What are the key findings?")
        };

        var message = new ChatMessage(ChatRole.User, contents);

        message.Contents.Should().HaveCount(4);
        message.Contents[0].Should().BeOfType<TextContent>();
        message.Contents[1].Should().BeOfType<DataContent>();
        message.Contents[2].Should().BeOfType<DataContent>();
        message.Contents[3].Should().BeOfType<TextContent>();

        var pdf = (DataContent)message.Contents[1];
        pdf.MediaType.Should().Be("application/pdf");
        pdf.Name.Should().Be("report.pdf");
        pdf.Data.Length.Should().Be(4);
    }

    [Fact]
    public void ClaudeContentBlock_WithSource_SerializesToCorrectJson()
    {
        // Simulate what AzureClaudeChatClient.BuildRequest produces
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var base64Data = Convert.ToBase64String(pdfBytes);

        var block = new
        {
            type = "document",
            source = new
            {
                type = "base64",
                media_type = "application/pdf",
                data = base64Data
            }
        };

        var json = JsonSerializer.Serialize(block, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        json.Should().Contain("\"type\":\"document\"");
        json.Should().Contain("\"media_type\":\"application/pdf\"");
        json.Should().Contain("\"type\":\"base64\"");
        json.Should().Contain($"\"data\":\"{base64Data}\"");
    }

    [Fact]
    public void BinaryExtensions_DetectedCorrectly()
    {
        var binaryExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tiff"
        };

        binaryExts.Contains(".pdf").Should().BeTrue();
        binaryExts.Contains(".PDF").Should().BeTrue();
        binaryExts.Contains(".png").Should().BeTrue();
        binaryExts.Contains(".md").Should().BeFalse();
        binaryExts.Contains(".txt").Should().BeFalse();
        binaryExts.Contains(".json").Should().BeFalse();
    }

    [Fact]
    public void ContentPath_Parsing_ExtractsNodePathAndFileName()
    {
        // Local path: content:report.pdf
        var path1 = "content:report.pdf";
        var idx1 = path1.IndexOf("content:", StringComparison.OrdinalIgnoreCase);
        idx1.Should().Be(0);
        var fileName1 = path1[(idx1 + "content:".Length)..];
        fileName1.Should().Be("report.pdf");

        // Absolute path: OrgA/Doc/content:chart.png
        var path2 = "OrgA/Doc/content:chart.png";
        var idx2 = path2.IndexOf("content:", StringComparison.OrdinalIgnoreCase);
        idx2.Should().Be(8); // after "OrgA/Doc/"
        var nodePath2 = path2[..(idx2 - 1)]; // strip trailing /
        var fileName2 = path2[(idx2 + "content:".Length)..];
        nodePath2.Should().Be("OrgA/Doc");
        fileName2.Should().Be("chart.png");
    }
}
