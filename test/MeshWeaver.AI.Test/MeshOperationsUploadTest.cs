#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Integration tests for <see cref="MeshOperations.Upload"/> — the shared core
/// that backs the MCP <c>upload</c> tool and the REST <c>POST /api/mesh/upload</c>
/// endpoint. Driving the core directly gives full coverage of every payload
/// shape (picture, document, nested path) without booting a TestServer.
///
/// <para>
/// Fixture mirrors the production "per-node FileSystem content collection"
/// pattern from <c>MeshPluginContentAccessTest</c>: every node hub registers
/// its own <c>content</c> collection rooted in a per-node temp directory,
/// matching <c>MemexConfiguration.ConfigureMemexMesh</c>.
/// </para>
/// </summary>
public class MeshOperationsUploadTest : MonolithMeshTestBase
{
    /// <summary>Share Mesh/SP across [Fact]s — same rationale as MeshPluginContentAccessTest.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");
    private static readonly string ContentBasePath = Path.Combine(
        Path.GetTempPath(),
        "MeshOperationsUploadTest_" + Guid.NewGuid().ToString("N"));
    private readonly string _testId = Guid.NewGuid().ToString("N")[..8];

    public MeshOperationsUploadTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddAI()
            // Per-node FileSystem content collection. IsEditable=true must be set
            // explicitly — the field defaults to false (matches bool type-default to
            // survive WhenWritingDefault serialization).
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                var contentDir = Path.Combine(ContentBasePath, nodePath);
                var contentConfig = new ContentCollectionConfig
                {
                    Name = "content",
                    SourceType = "FileSystem",
                    IsEditable = true,
                    BasePath = contentDir,
                    Settings = new Dictionary<string, string> { ["BasePath"] = contentDir },
                };
                // Second collection — same shape, but read-only. Lets us assert that
                // Upload refuses to write into a non-editable collection. IsEditable
                // defaults to false; we leave it unset.
                var readOnlyConfig = new ContentCollectionConfig
                {
                    Name = "frozen",
                    SourceType = "FileSystem",
                    BasePath = contentDir,
                    Settings = new Dictionary<string, string> { ["BasePath"] = contentDir },
                };
                config = config
                    .AddContentCollection(_ => contentConfig)
                    .AddContentCollection(_ => readOnlyConfig);
                return config.AddDefaultLayoutAreas();
            });

    // ---- Helpers ---------------------------------------------------------

    /// <summary>The hub the upload core resolves <c>IPathResolver</c> /
    /// <c>IContentService</c> from — same one tests use to issue requests.</summary>
    private MeshOperations Ops() => new(GetClient());

    private async Task<string> CreateTestNodeAsync(string suffix)
    {
        var nodePath = $"Upload_{_testId}_{suffix}";
        // FileSystemStreamProvider.InitializeAsync enumerates the base directory; create it
        // upfront so the first SaveFileAsync doesn't fail with DirectoryNotFoundException.
        // Matches the pattern in MeshPluginContentAccessTest.
        Directory.CreateDirectory(Path.Combine(ContentBasePath, nodePath));
        await NodeFactory.CreateNode(
            new MeshNode(nodePath) { Name = $"Upload test {suffix}", NodeType = "Markdown" });
        return nodePath;
    }

    private static byte[] MakePng()
    {
        // 1×1 transparent PNG — a real PNG signature is what content-type sniffers
        // look at, and any byte-equality assertion still works on this minimal payload.
        return
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
            0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
            0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82,
        ];
    }

    private static byte[] MakeSvg() => Encoding.UTF8.GetBytes(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><circle cx=\"5\" cy=\"5\" r=\"4\"/></svg>");

    private static byte[] MakeRandomBytes(int size)
    {
        var b = new byte[size];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static string DiskPath(string nodePath, string relative) =>
        Path.Combine(ContentBasePath, nodePath, relative.Replace('/', Path.DirectorySeparatorChar));

    private static JsonElement ParseStatus(string result)
    {
        try
        {
            return JsonDocument.Parse(result).RootElement;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Upload returned non-JSON ({ex.Message}). Raw result: {result}", ex);
        }
    }

    // ---- Picture uploads ------------------------------------------------

    [Fact]
    public async Task Upload_Png_LandsInContentCollection()
    {
        var nodePath = await CreateTestNodeAsync("png");
        var bytes = MakePng();

        var result = await Ops().Upload($"{nodePath}/content/logo.png", bytes)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        Output.WriteLine(result);

        var json = ParseStatus(result);
        json.GetProperty("status").GetString().Should().Be("Uploaded");
        json.GetProperty("bytes").GetInt32().Should().Be(bytes.Length);
        json.GetProperty("path").GetString().Should().Be($"{nodePath}/content/logo.png");

        // Byte-for-byte verification on disk — the upload tool's reason for being.
        var disk = DiskPath(nodePath, "logo.png");
        File.Exists(disk).Should().BeTrue();
        (await File.ReadAllBytesAsync(disk, TestContext.Current.CancellationToken))
            .Should().Equal(bytes);
    }

    [Fact]
    public async Task Upload_Svg_PreservesUtf8Markup()
    {
        var nodePath = await CreateTestNodeAsync("svg");
        var bytes = MakeSvg();

        var result = await Ops().Upload($"{nodePath}/content/icon.svg", bytes)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        ParseStatus(result).GetProperty("status").GetString().Should().Be("Uploaded");

        var disk = DiskPath(nodePath, "icon.svg");
        var roundTrip = await File.ReadAllTextAsync(disk, Encoding.UTF8, TestContext.Current.CancellationToken);
        roundTrip.Should().Contain("<circle");
        roundTrip.Should().Contain("viewBox");
    }

    [Fact]
    public async Task Upload_Png_NestedSubfolderPath()
    {
        var nodePath = await CreateTestNodeAsync("nested");
        var bytes = MakePng();

        var result = await Ops().Upload($"{nodePath}/content/branding/logos/dark.png", bytes)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        ParseStatus(result).GetProperty("status").GetString().Should().Be("Uploaded");

        var disk = DiskPath(nodePath, "branding/logos/dark.png");
        File.Exists(disk).Should().BeTrue();
        (await File.ReadAllBytesAsync(disk, TestContext.Current.CancellationToken))
            .Should().Equal(bytes);
    }

    // ---- Document uploads ----------------------------------------------

    [Fact]
    public async Task Upload_Docx_RoundTripsBytes()
    {
        // Random bytes with a .docx extension — the upload tool is content-agnostic;
        // we want to prove a "real-sized" document blob survives the round-trip exactly.
        var nodePath = await CreateTestNodeAsync("docx");
        var bytes = MakeRandomBytes(64 * 1024); // 64 KB

        var result = await Ops().Upload($"{nodePath}/content/proposal.docx", bytes)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        var json = ParseStatus(result);
        json.GetProperty("status").GetString().Should().Be("Uploaded");
        json.GetProperty("bytes").GetInt32().Should().Be(bytes.Length);

        var disk = DiskPath(nodePath, "proposal.docx");
        (await File.ReadAllBytesAsync(disk, TestContext.Current.CancellationToken))
            .Should().Equal(bytes);
    }

    [Fact]
    public async Task Upload_Xlsx_RoundTripsBytes()
    {
        var nodePath = await CreateTestNodeAsync("xlsx");
        var bytes = MakeRandomBytes(32 * 1024);

        var result = await Ops().Upload($"{nodePath}/content/model.xlsx", bytes)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        ParseStatus(result).GetProperty("status").GetString().Should().Be("Uploaded");
        (await File.ReadAllBytesAsync(DiskPath(nodePath, "model.xlsx"), TestContext.Current.CancellationToken))
            .Should().Equal(bytes);
    }

    [Fact]
    public async Task Upload_Pdf_RoundTripsBytes()
    {
        var nodePath = await CreateTestNodeAsync("pdf");
        // Mix a real PDF magic header with random payload — exercises the binary path
        // and gives a sniffable signature.
        var pdf = new byte[8 * 1024];
        RandomNumberGenerator.Fill(pdf);
        var header = "%PDF-1.4\n"u8;
        header.CopyTo(pdf);

        var result = await Ops().Upload($"{nodePath}/content/spec.pdf", pdf)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        ParseStatus(result).GetProperty("status").GetString().Should().Be("Uploaded");
        (await File.ReadAllBytesAsync(DiskPath(nodePath, "spec.pdf"), TestContext.Current.CancellationToken))
            .Should().Equal(pdf);
    }

    // ---- MCP-style base64 path (drift check) ---------------------------

    /// <summary>
    /// MCP's <c>Upload</c> tool decodes <c>base64Content</c> and forwards to the
    /// same <see cref="MeshOperations.Upload"/>. Run the same flow here to prove
    /// the two transports stay in sync — both produce the same on-disk bytes
    /// and the same response shape.
    /// </summary>
    [Fact]
    public async Task Upload_Base64Path_MatchesRawBytesPath()
    {
        var nodePath = await CreateTestNodeAsync("base64");
        var bytes = MakePng();
        var base64 = Convert.ToBase64String(bytes);

        // Decode at the boundary (this is exactly what McpMeshPlugin.Upload does)
        // and hand off to the shared core.
        var decoded = Convert.FromBase64String(base64);
        var result = await Ops().Upload($"{nodePath}/content/logo-b64.png", decoded)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        var json = ParseStatus(result);
        json.GetProperty("status").GetString().Should().Be("Uploaded");
        json.GetProperty("bytes").GetInt32().Should().Be(bytes.Length);

        (await File.ReadAllBytesAsync(DiskPath(nodePath, "logo-b64.png"), TestContext.Current.CancellationToken))
            .Should().Equal(bytes);
    }

    // ---- Error paths ----------------------------------------------------

    [Fact]
    public async Task Upload_EmptyPath_ReturnsError()
    {
        var result = await Ops().Upload("", MakePng())
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        result.Should().StartWith("Error:");
        result.Should().Contain("path is required");
    }

    [Fact]
    public async Task Upload_EmptyBytes_ReturnsError()
    {
        var nodePath = await CreateTestNodeAsync("nobytes");
        var result = await Ops().Upload($"{nodePath}/content/empty.bin", Array.Empty<byte>())
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        result.Should().StartWith("Error:");
        result.Should().Contain("content is required");
    }

    [Fact]
    public async Task Upload_MissingFilename_ReturnsError()
    {
        var nodePath = await CreateTestNodeAsync("nofile");
        // Path ends in collection only, no filename — Upload must reject early.
        var result = await Ops().Upload($"{nodePath}/content/", MakePng())
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task Upload_UnknownCollection_ReturnsError()
    {
        var nodePath = await CreateTestNodeAsync("unknown");
        var result = await Ops().Upload($"{nodePath}/nonexistent/file.png", MakePng())
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        result.Should().StartWith("Error:");
        result.Should().Contain("nonexistent");
    }

    /// <summary>
    /// Upload refuses to write into a collection where <c>IsEditable = false</c>
    /// (the "frozen" fixture leaves IsEditable at its default false).
    /// </summary>
    [Fact]
    public async Task Upload_ReadOnlyCollection_Refused()
    {
        var nodePath = await CreateTestNodeAsync("frozen");
        var result = await Ops().Upload($"{nodePath}/frozen/should-fail.png", MakePng())
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        result.Should().StartWith("Error:");
        result.Should().Contain("read-only");
        File.Exists(DiskPath(nodePath, "should-fail.png")).Should().BeFalse();
    }
}
