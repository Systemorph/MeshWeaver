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

        // UTF-32 text with a byte-order mark, both byte orders. UTF-32LE's mark (FF FE 00 00) begins
        // with the UTF-16LE mark, so the 4-byte marks must be checked first.
        File.WriteAllBytes(Path.Combine(_contentBasePath, "utf32le.txt"), new UTF32Encoding(false, true).GetPreamble()
            .Concat(new UTF32Encoding(false, true).GetBytes(Utf32Text)).ToArray());
        File.WriteAllBytes(Path.Combine(_contentBasePath, "utf32be.txt"), new UTF32Encoding(true, true).GetPreamble()
            .Concat(new UTF32Encoding(true, true).GetBytes(Utf32Text)).ToArray());

        // A protobuf message: valid UTF-8, no NUL — and still binary (control bytes 08 01 12 03).
        File.WriteAllBytes(Path.Combine(_contentBasePath, "message.pb"), [0x08, 0x01, 0x12, 0x03, 0x61, 0x62, 0x63]);

        // Latin-1 text whose first non-ASCII byte lies AFTER the 8 KiB sample: the sample alone reads
        // as UTF-8, so the encoding must be decided over the whole bounded head.
        File.WriteAllBytes(Path.Combine(_contentBasePath, "late-latin1.csv"),
            Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("row,value\n", 1000)))
                .Concat(new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n' }).ToArray());

        Directory.CreateDirectory(_pipedBasePath);
    }

    private const string Utf32Text = "héllo 🌍\n";
    private const string NonSeekableSourceType = "NonSeekableTest";
    private readonly string _pipedBasePath = Path.Combine(AppContext.BaseDirectory, "Files", "BinaryGuardTestPiped");

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .WithServices(services => services
                .AddSingleton<IContentTransformer, DocxTestTransformer>()
                .AddKeyedScoped<IStreamProviderFactory, EndlessStreamFactory>(NonSeekableSourceType))
            .AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = "content",
                SourceType = "FileSystem",
                IsEditable = true,
                ExposeInChildren = true,
                BasePath = _contentBasePath,
                Settings = new Dictionary<string, string> { ["BasePath"] = _contentBasePath }
            })
            .AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = "piped",
                SourceType = NonSeekableSourceType,
                BasePath = _pipedBasePath,
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

    [Theory]
    [InlineData("utf32le.txt")]
    [InlineData("utf32be.txt")]
    public async Task Utf32_Text_With_A_Byte_Order_Mark_Is_Decoded_As_Utf32(string file)
    {
        var result = await Files.GetFileContent("content", file).Should().Emit();

        Output.WriteLine(result.Content ?? result.Error ?? "<null>");
        result.Success.Should().BeTrue();
        result.Content.Should().Be(Utf32Text);
    }

    [Fact]
    public async Task Valid_Utf8_With_Control_Bytes_And_No_Nul_Is_Binary()
    {
        var result = await Files.GetFileContent("content", "message.pb").Should().Emit();

        Output.WriteLine(result.Content ?? result.Error ?? "<null>");
        result.Success.Should().BeTrue();
        result.Content.Should().StartWith("[binary file: message.pb");
        result.Content.Should().Contain("control bytes");
        result.Content.Should().Contain("7 bytes");
    }

    [Fact]
    public async Task Encoding_Is_Decided_Over_The_Whole_Head_Not_The_Sample()
    {
        var result = await Files.GetFileContent("content", "late-latin1.csv").Should().Emit();

        result.Success.Should().BeTrue();
        var content = result.Content!;
        Output.WriteLine($"tail={content[^20..]}");
        content.Should().NotContain("\uFFFD", "a Latin-1 byte past the sample must not be decoded as UTF-8");
        content.Should().EndWith("café\n");
    }

    [Fact]
    public async Task Binary_Refusal_On_A_NonSeekable_Stream_Does_Not_Drain_It_And_Reports_An_Unknown_Size()
    {
        // The stream never ends: draining it to measure its size would never return.
        var result = await Files.GetFileContent("piped", "endless.bin")
            .Should().Within(TimeSpan.FromSeconds(10))
            .Emit("a refusal reads the sample only — it never drains the stream to count its size",
                TestContext.Current.CancellationToken);

        Output.WriteLine(result.Content ?? result.Error ?? "<null>");
        result.Success.Should().BeTrue();
        result.Content.Should().StartWith("[binary file: endless.bin");
        result.Content.Should().Contain("size unknown");
        result.Content.Should().NotContain("-1 bytes");
    }

    [Fact]
    public async Task Truncated_Text_On_A_NonSeekable_Stream_Does_Not_Drain_It_And_Says_The_Size_Is_Unknown()
    {
        var result = await Files.GetFileContent("piped", "endless.txt")
            .Should().Within(TimeSpan.FromSeconds(10))
            .Emit("a bounded read stops at the bound — it never drains the stream to count its size",
                TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var content = result.Content!;
        Output.WriteLine($"length={content.Length}; tail={content[^200..]}");
        content.Should().Contain("[truncated: endless.txt is larger than 262144 bytes (total size unknown)");
        content.Should().NotContain("-1 bytes");
    }

    /// <summary>Hands out <see cref="EndlessStreamProvider"/> over an empty directory.</summary>
    private sealed class EndlessStreamFactory : IStreamProviderFactory
    {
        public IObservable<IStreamProvider> Create(ContentCollectionConfig config)
            => System.Reactive.Linq.Observable.Return<IStreamProvider>(
                new EndlessStreamProvider(new FileSystemStreamProvider(config.BasePath ?? "")));
    }

    /// <summary>
    /// A real file-system provider whose file reads answer a NON-SEEKABLE stream that never ends —
    /// the shape of a piped or remote store that reports no length. <c>.bin</c> yields NUL bytes,
    /// anything else yields text lines.
    /// </summary>
    private sealed class EndlessStreamProvider(IStreamProvider inner) : IStreamProvider
    {
        public string ProviderType => NonSeekableSourceType;

        public Task<Stream?> GetStreamAsync(string reference, CancellationToken ct = default)
            => Task.FromResult<Stream?>(new EndlessStream(reference.EndsWith(".bin") ? (byte)0 : (byte)'a'));
        public IDisposable? AttachMonitor(Action<string> onChanged) => inner.AttachMonitor(onChanged);
        public Task<(Stream? Stream, string Path, DateTime LastModified)> GetStreamWithMetadataAsync(
            string path, CancellationToken ct = default) => inner.GetStreamWithMetadataAsync(path, ct);
        public Task WriteStreamAsync(string reference, Stream content, CancellationToken ct = default)
            => inner.WriteStreamAsync(reference, content, ct);
        public IAsyncEnumerable<(Stream? Stream, string Path, DateTime LastModified)> GetStreamsAsync(
            Func<string, bool> filter, CancellationToken ct = default) => inner.GetStreamsAsync(filter, ct);
        public IAsyncEnumerable<FolderItem> GetFolders(string path, CancellationToken ct = default)
            => inner.GetFolders(path, ct);
        public IAsyncEnumerable<FileItem> GetFiles(string path, CancellationToken ct = default)
            => inner.GetFiles(path, ct);
        public Task SaveFileAsync(string path, string fileName, Stream content, CancellationToken ct = default)
            => inner.SaveFileAsync(path, fileName, content, ct);
        public Task CreateFolderAsync(string folderPath) => inner.CreateFolderAsync(folderPath);
        public Task DeleteFolderAsync(string folderPath) => inner.DeleteFolderAsync(folderPath);
        public Task DeleteFileAsync(string filePath) => inner.DeleteFileAsync(filePath);
        public Task<System.Collections.Immutable.ImmutableDictionary<string, Author>> LoadAuthorsAsync(
            CancellationToken ct = default) => inner.LoadAuthorsAsync(ct);
    }

    /// <summary>A read-only, non-seekable stream that never reaches its end.</summary>
    private sealed class EndlessStream(byte fill) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++)
                buffer[offset + i] = fill != 0 && i % 64 == 63 ? (byte)'\n' : fill;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DocxTestTransformer : IContentTransformer
    {
        public IReadOnlyCollection<string> SupportedExtensions { get; } = [".docx"];

        public Task<string> TransformToMarkdownAsync(Stream input, CancellationToken ct = default)
            => Task.FromResult(ConvertedMarkdown);
    }
}
