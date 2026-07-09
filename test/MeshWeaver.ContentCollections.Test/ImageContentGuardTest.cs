using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// The image guard in <see cref="FileContentProvider"/>: a binary image with no text transformer is
/// NEVER decoded into text — reading it returns a short, informative placeholder (name + media type +
/// size), never the raw bytes. This is the safety net behind issue #379 (raw multi-MB binary flooding a
/// model context and failing the next request with 400).
/// </summary>
public class ImageContentGuardTest : HubTestBase
{
    private readonly string _contentBasePath = Path.Combine(AppContext.BaseDirectory, "Files", "ImageGuardTest");

    public ImageContentGuardTest(ITestOutputHelper output) : base(output)
    {
        Directory.CreateDirectory(_contentBasePath);
        // Content need not be a valid image; the guard keys on the extension. The leading PNG magic
        // bytes prove the guard does NOT decode/return them.
        File.WriteAllBytes(
            Path.Combine(_contentBasePath, "photo.png"),
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0xFF]);
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
    public async Task FileContentProvider_Guards_Image_Returns_Placeholder_Not_Bytes()
    {
        var hub = GetClient();
        var fileContentProvider = hub.ServiceProvider.GetRequiredService<IFileContentProvider>();

        var result = await fileContentProvider.GetFileContent("content", "photo.png")
            .Should().Emit();

        Output.WriteLine($"Image content result:\n{result.Content}");
        result.Success.Should().BeTrue();
        // A short, informative placeholder — NEVER the raw image bytes (issue #379).
        result.Content.Should().Contain("[image: photo.png");
        result.Content.Should().Contain("image/png");
        result.Content.Should().NotContain("�", "raw binary must not be decoded into replacement chars");
    }
}
