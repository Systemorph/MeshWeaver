using System.IO;
using System.Text;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Regression pins for #326: the <c>/static/{**path}</c> file-serving endpoint must URL-decode the
/// captured path before the content-collection lookup. ASP.NET Core leaves catch-all route values
/// percent-encoded (so the path's <c>/</c> separators survive), so a file under a folder with a
/// space or umlaut (e.g. <c>Reports/Data Extraction/Übersicht 2025.pdf</c>) arrives as
/// <c>Reports/Data%20Extraction/%C3%9Cbersicht%202025.pdf</c> and misses the stored path unless
/// <see cref="BlazorHostingExtensions.DecodeContentPath"/> restores it first.
/// </summary>
public class ContentPathDecodingTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly string _basePath =
        Path.Combine(Path.GetTempPath(), "mw-content-decode-" + Guid.NewGuid().ToString("N"));

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration configuration)
        => configuration
            .AddContentCollections()
            .AddFileSystemContentCollection("content", _ => _basePath);

    [Theory]
    // Space + umlaut — the exact shape from the issue's evidence.
    [InlineData("Data%20Extraction/%C3%9Cbersicht%202025.pdf", "Data Extraction/Übersicht 2025.pdf")]
    [InlineData("Reports/Data%20Extraction/%C3%9Cbersicht%202025.pdf", "Reports/Data Extraction/Übersicht 2025.pdf")]
    // Lower-case umlaut escape.
    [InlineData("Berichte/%C3%BCbersicht.pdf", "Berichte/übersicht.pdf")]
    // Plain ASCII path — decoding must be a no-op (never mangles a space-free path).
    [InlineData("Reports/2025/summary.pdf", "Reports/2025/summary.pdf")]
    // A name literally containing "%20" — encoded as %20%2520%20 by Uri.EscapeDataString.
    // Single-decode restores "Data %20 Extraction.pdf"; a DOUBLE-decode would wrongly collapse
    // the inner %20 to a space ("Data   Extraction.pdf"), so this pins the decode-exactly-once.
    [InlineData("Data%20%2520%20Extraction.pdf", "Data %20 Extraction.pdf")]
    public void DecodeContentPath_RestoresEscapedSegments(string encoded, string expected)
    {
        BlazorHostingExtensions.DecodeContentPath(encoded).Should().Be(expected);
    }

    [Fact]
    public async Task ServingPath_WithSpaceAndUmlaut_ResolvesAfterDecoding()
    {
        // Arrange — a real file-backed collection holding a file whose folder + name contain a
        // space AND an umlaut, exactly like the issue's Reports/Data Extraction/Übersicht 2025.pdf.
        var storedPath = "Reports/Data Extraction/Übersicht 2025.pdf";
        var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7 test payload for #326");
        var fullPath = Path.Combine(_basePath, "Reports", "Data Extraction", "Übersicht 2025.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, pdfBytes, TestContext.Current.CancellationToken);

        var contentService = Mesh.ServiceProvider.GetRequiredService<IContentService>();
        var collection = await contentService.GetCollection("content").FirstAsync().ToTask(TestContext.Current.CancellationToken);
        collection.Should().NotBeNull();

        // The path as it reaches the /static/{**path} endpoint: each segment percent-encoded
        // (Uri.EscapeDataString), '/' separators preserved — the catch-all is NOT auto-decoded.
        var encodedPath = "Reports/Data%20Extraction/%C3%9Cbersicht%202025.pdf";

        // Act 1 — the raw encoded path misses (this is the reported bug).
        await using (var missed = await collection!.GetContent(encodedPath).FirstAsync().ToTask(TestContext.Current.CancellationToken))
        {
            missed.Should().BeNull("the percent-encoded path does not match the stored file — this is the #326 defect");
        }

        // Act 2 — decoding at the endpoint boundary yields the real stored path...
        var decodedPath = BlazorHostingExtensions.DecodeContentPath(encodedPath);
        decodedPath.Should().Be(storedPath);

        // ...and the decoded path resolves the file with its bytes intact.
        await using var stream = await collection.GetContent(decodedPath).FirstAsync().ToTask(TestContext.Current.CancellationToken);
        stream.Should().NotBeNull("the decoded path matches the stored file");
        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms, TestContext.Current.CancellationToken);
        ms.ToArray().Should().Equal(pdfBytes);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(_basePath))
                Directory.Delete(_basePath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }
}
