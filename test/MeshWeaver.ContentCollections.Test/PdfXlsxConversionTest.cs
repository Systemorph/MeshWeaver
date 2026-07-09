using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// Tests for the PDF (<see cref="PdfPigContentTransformer"/>) and XLSX
/// (<see cref="ClosedXmlContentTransformer"/>) readers restored to the shared
/// <see cref="IContentTransformer"/> seam (issue #379). The key guarantee is that a binary
/// document read through the content-collection file path — the SINGLE path both the agent
/// <c>Get</c> tool (<c>MeshPlugin.Get</c>) and the MCP <c>get</c> tool (<c>McpMeshPlugin.Get</c>)
/// flow through via <c>MeshOperations.Get</c> → <see cref="IFileContentProvider"/> — returns
/// extracted text, never the raw <c>%PDF…</c> / OpenXML-zip bytes.
/// </summary>
public class PdfXlsxConversionTest : HubTestBase
{
    private readonly string _contentBasePath = Path.Combine(AppContext.BaseDirectory, "Files", "PdfXlsxTest");

    public PdfXlsxConversionTest(ITestOutputHelper output) : base(output)
    {
        // Create fixtures in the ctor (not ConfigureClient): the direct-transformer tests never
        // build a hub, so the files must exist regardless of whether GetClient() ran — mirrors
        // the ordering fix documented in DocxConversionTest.
        Directory.CreateDirectory(_contentBasePath);
        File.WriteAllBytes(
            Path.Combine(_contentBasePath, "report.pdf"),
            OnePagePdf("Quarterly report", "Revenue grew across all regions."));
        File.WriteAllBytes(
            Path.Combine(_contentBasePath, "figures.xlsx"),
            OneSheetXlsx("Figures",
                ["Region", "Revenue"],
                ["EMEA", "1000"],
                ["APAC", "2500"]));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = "content",
                SourceType = "FileSystem",
                IsEditable = true,
                ExposeInChildren = true,
                BasePath = _contentBasePath,
                Settings = new Dictionary<string, string> { ["BasePath"] = _contentBasePath }
            });
    }

    [Fact]
    public async Task PdfPigContentTransformer_Converts_Pdf_To_Text()
    {
        var transformer = new PdfPigContentTransformer();
        transformer.SupportedExtensions.Should().Contain(".pdf");

        await using var stream = File.OpenRead(Path.Combine(_contentBasePath, "report.pdf"));
        var text = await transformer.TransformToMarkdownAsync(stream, TestContext.Current.CancellationToken);

        Output.WriteLine($"Extracted PDF text:\n{text}");
        text.Should().Contain("Quarterly report");
        text.Should().Contain("Revenue grew across all regions");
        text.Should().NotContain("%PDF", "the raw PDF byte stream must never leak through");
    }

    [Fact]
    public async Task ClosedXmlContentTransformer_Converts_Xlsx_To_Markdown()
    {
        var transformer = new ClosedXmlContentTransformer();
        transformer.SupportedExtensions.Should().Contain(".xlsx");

        await using var stream = File.OpenRead(Path.Combine(_contentBasePath, "figures.xlsx"));
        var markdown = await transformer.TransformToMarkdownAsync(stream, TestContext.Current.CancellationToken);

        Output.WriteLine($"Converted XLSX markdown:\n{markdown}");
        markdown.Should().Contain("## Sheet: Figures");
        markdown.Should().Contain("Region");
        markdown.Should().Contain("Revenue");
        markdown.Should().Contain("EMEA");
        markdown.Should().Contain("2500");
        // Column-letter header row proves the markdown-table shape, not a raw dump.
        markdown.Should().Contain("| Row | A | B |");
    }

    [Fact]
    public async Task FileContentProvider_Auto_Converts_Pdf()
    {
        var hub = GetClient();
        var fileContentProvider = hub.ServiceProvider.GetRequiredService<IFileContentProvider>();

        var result = await fileContentProvider.GetFileContent("content", "report.pdf")
            .Should().Emit();

        Output.WriteLine($"PDF content result:\n{result.Content}");
        result.Success.Should().BeTrue();
        result.Content.Should().Contain("Quarterly report");
        result.Content.Should().NotContain("%PDF", "the file-read seam must serve text, not raw bytes");
    }

    [Fact]
    public async Task FileContentProvider_Auto_Converts_Xlsx()
    {
        var hub = GetClient();
        var fileContentProvider = hub.ServiceProvider.GetRequiredService<IFileContentProvider>();

        var result = await fileContentProvider.GetFileContent("content", "figures.xlsx")
            .Should().Emit();

        Output.WriteLine($"XLSX content result:\n{result.Content}");
        result.Success.Should().BeTrue();
        result.Content.Should().Contain("## Sheet: Figures");
        result.Content.Should().Contain("EMEA");
    }

    [Fact]
    public async Task GetDataRequest_Content_Prefix_Returns_Text_For_Pdf()
    {
        // The unified path content:content/report.pdf is exactly what MeshOperations.Get posts
        // for both the agent and the MCP Get — asserting here proves the end-to-end route.
        var hub = GetClient();

        var response = await hub.Observe(
            new GetDataRequest(new UnifiedReference("content:content/report.pdf")),
            o => o.WithTarget(hub.Address)).Should().Within(10.Seconds()).Emit();

        var dataResponse = response.Message;
        dataResponse.Error.Should().BeNull();
        dataResponse.Data.Should().NotBeNull();
        dataResponse.Data!.ToString().Should().Contain("Quarterly report");
        dataResponse.Data!.ToString().Should().NotContain("%PDF");
    }

    [Fact]
    public void The_Binary_Document_Extensions_Are_Each_Covered_By_Exactly_One_Transformer()
    {
        // Encodes the "defined once" guarantee: the read path is a single shared seam
        // (IContentTransformer resolved from DI), so the agent Get and MCP get behave 1:1.
        var hub = GetClient();
        var transformers = hub.ServiceProvider.GetServices<IContentTransformer>().ToList();

        foreach (var ext in new[] { ".pdf", ".docx", ".xlsx" })
            transformers.Count(t => t.SupportedExtensions.Contains(ext))
                .Should().Be(1, $"exactly one transformer should own '{ext}'");
    }

    private static byte[] OnePagePdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842); // A4 in points.

        var y = 800;
        foreach (var line in lines)
        {
            page.AddText(line, 12, new PdfPoint(50, y), font);
            y -= 20;
        }

        return builder.Build();
    }

    private static byte[] OneSheetXlsx(string sheetName, params string[][] rows)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.AddWorksheet(sheetName);
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                ws.Cell(r + 1, c + 1).Value = rows[r][c];

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }
}
