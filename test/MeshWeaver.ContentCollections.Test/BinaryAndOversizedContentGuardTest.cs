using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// Issue #7064: a content-file read with no transformer used to decode ANY file raw as text, with no
/// binary check and no size bound — an agent's <c>Get</c> of a <c>.pptx</c> put the ZIP container's
/// bytes into the conversation and the next model request was refused by the provider's 8 MB cap.
/// Pinned here: a binary file (known type, or detected by content) answers a named placeholder with
/// its type and size, never bytes; an oversized text file answers its head plus a truncation marker
/// stating the total size; ordinary text, row limits and a registered converter are unchanged.
/// </summary>
public class BinaryAndOversizedContentGuardTest : HubTestBase
{
    private const int OversizedBytes = 300 * 1024;
    private const string ConvertedMarkdown = "# Converted by the test transformer";

    private readonly string _contentBasePath = Path.Combine(AppContext.BaseDirectory, "Files", "BinaryGuardTest");

    public BinaryAndOversizedContentGuardTest(ITestOutputHelper output) : base(output)
    {
        Directory.CreateDirectory(_contentBasePath);

        // A real ZIP container, as a .pptx is: PK header, deflated entries, NUL-laden central directory.
        var pptx = Path.Combine(_contentBasePath, "deck.pptx");
        File.Delete(pptx);
        using (var zip = ZipFile.Open(pptx, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("ppt/slides/slide1.xml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("<p:sld><a:t>" + new string('x', 4096) + "</a:t></p:sld>");
        }

        // An unknown extension whose bytes are binary — detected by content, not by name.
        File.WriteAllBytes(Path.Combine(_contentBasePath, "blob.dat"),
            [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x00, 0x00, 0x03, 0xFF, 0xFE, 0x10]);

        // A text file larger than the read bound.
        File.WriteAllText(Path.Combine(_contentBasePath, "big.txt"),
            string.Concat(Enumerable.Repeat("0123456789abcdef", OversizedBytes / 16)));

        File.WriteAllText(Path.Combine(_contentBasePath, "readme.md"), "# Grüezi\n\nPlain UTF-8 text — unchanged.\n");
        File.WriteAllText(Path.Combine(_contentBasePath, "rows.csv"), "a,b\n1,2\n3,4\n5,6\n7,8\n");
        // Legacy single-byte text (Latin-1 'é' = 0xE9) is text, not binary.
        File.WriteAllBytes(Path.Combine(_contentBasePath, "legacy.csv"), [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n']);
        File.WriteAllBytes(Path.Combine(_contentBasePath, "report.docx"), [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]);
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .WithServices(services => services.AddSingleton<IContentTransformer, DocxTestTransformer>())
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

    private IFileContentProvider Files => GetClient().ServiceProvider.GetRequiredService<IFileContentProvider>();

    [Fact]
    public async Task Pptx_Returns_Named_Placeholder_Not_Bytes()
    {
        var size = new FileInfo(Path.Combine(_contentBasePath, "deck.pptx")).Length;

        var result = await Files.GetFileContent("content", "deck.pptx").Should().Emit();

        Output.WriteLine(result.Content ?? result.Error ?? "<null>");
        result.Success.Should().BeTrue();
        result.Content.Should().StartWith("[binary file: deck.pptx");
        result.Content.Should().Contain("PowerPoint presentation");
        result.Content.Should().Contain($"{size} bytes");
        result.Content.Should().NotContain("PK\u0003\u0004", "the ZIP container's bytes must never be decoded into text");
        result.Content.Should().NotContain("\uFFFD", "no byte may be decoded into a replacement character");
    }

    [Fact]
    public async Task Unknown_Extension_With_Binary_Content_Returns_Named_Placeholder()
    {
        var result = await Files.GetFileContent("content", "blob.dat").Should().Emit();

        Output.WriteLine(result.Content ?? result.Error ?? "<null>");
        result.Success.Should().BeTrue();
        result.Content.Should().StartWith("[binary file: blob.dat");
        result.Content.Should().Contain("NUL bytes");
        result.Content.Should().Contain("12 bytes");
        result.Content.Should().NotContain("\u007FELF");
    }

    [Fact]
    public async Task Oversized_Text_Is_Truncated_With_A_Marker_Stating_The_Total_Size()
    {
        var result = await Files.GetFileContent("content", "big.txt").Should().Emit();

        result.Success.Should().BeTrue();
        var content = result.Content!;
        Output.WriteLine($"length={content.Length}; tail={content[^200..]}");
        content.Length.Should().BeLessThan(OversizedBytes, "a text read is bounded");
        content.Should().StartWith("0123456789abcdef");
        content.Should().Contain($"[truncated: big.txt is {OversizedBytes} bytes");
    }

    [Fact]
    public async Task Normal_Text_Is_Returned_Unchanged()
    {
        var result = await Files.GetFileContent("content", "readme.md").Should().Emit();

        result.Success.Should().BeTrue();
        result.Content.Should().Be("# Grüezi\n\nPlain UTF-8 text — unchanged.\n");
    }

    [Fact]
    public async Task Row_Limit_Still_Returns_The_First_Rows_Without_A_Marker()
    {
        var result = await Files.GetFileContent("content", "rows.csv", 2).Should().Emit();

        result.Success.Should().BeTrue();
        result.Content.Should().Be("a,b" + Environment.NewLine + "1,2" + Environment.NewLine);
    }

    [Fact]
    public async Task Legacy_Single_Byte_Text_Is_Text_Not_Binary()
    {
        var result = await Files.GetFileContent("content", "legacy.csv").Should().Emit();

        result.Success.Should().BeTrue();
        result.Content.Should().NotStartWith("[binary file:");
        result.Content.Should().StartWith("caf");
    }

    [Fact]
    public async Task Registered_Converter_Still_Wins_For_Its_Extension()
    {
        var result = await Files.GetFileContent("content", "report.docx").Should().Emit();

        result.Success.Should().BeTrue();
        result.Content.Should().Be(ConvertedMarkdown);
    }

    private sealed class DocxTestTransformer : IContentTransformer
    {
        public IReadOnlyCollection<string> SupportedExtensions { get; } = [".docx"];

        public Task<string> TransformToMarkdownAsync(Stream input, CancellationToken ct = default)
            => Task.FromResult(ConvertedMarkdown);
    }
}
