using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// A staged blob (<see cref="ContentStaging.Folder"/>) is bytes the owner has not admitted yet, so the
/// hub's own content surface — <see cref="IFileContentProvider"/>, which <c>GetData</c> and the agents'
/// file tools read through — neither lists the staging folder, nor lists inside it, nor reads a blob
/// from it (#6585 review). The visible file beside it is the negative control: the same calls DO list
/// and read ordinary content, so the absences are about the folder, not a broken collection.
/// </summary>
public class StagedBlobIsNotContentTest : HubTestBase
{
    private const string Handle = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _contentBasePath = Path.Combine(
        AppContext.BaseDirectory, "Files", "StagedBlobIsNotContent", Guid.NewGuid().ToString("N"));

    public StagedBlobIsNotContentTest(ITestOutputHelper output) : base(output)
    {
        Directory.CreateDirectory(Path.Combine(_contentBasePath, ContentStaging.Folder));
        File.WriteAllText(Path.Combine(_contentBasePath, ContentStaging.Folder, Handle), "staged, not admitted");
        File.WriteAllText(Path.Combine(_contentBasePath, "visible.txt"), "admitted content");
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = "content",
                SourceType = "FileSystem",
                IsEditable = true,
                ExposeInChildren = true,
                BasePath = _contentBasePath,
                Settings = new Dictionary<string, string> { ["BasePath"] = _contentBasePath }
            });

    [Fact]
    public async Task AStagedBlob_IsNeitherListedNorRead_ThroughTheFileContentProvider()
    {
        var files = GetClient().ServiceProvider.GetRequiredService<IFileContentProvider>();

        var root = await files.ListCollectionItems("content", "/").Should().Emit();
        var inside = await files.ListCollectionItems("content", $"/{ContentStaging.Folder}").Should().Emit();
        var blob = await files.GetFileContent("content", ContentStaging.BlobPath(Handle)).Should().Emit();
        var visible = await files.GetFileContent("content", "visible.txt").Should().Emit();
        Output.WriteLine($"root: {string.Join(", ", root.Items?.Select(i => i.Path) ?? [])}\ninside: {inside.Error}\nblob: {blob.Error}");

        root.Success.Should().BeTrue();
        root.Items!.Select(i => i.Name).Should().Contain("visible.txt",
            "the root lists ordinary content — else the absence below proves nothing")
            .And.NotContain(ContentStaging.Folder, "the staging folder is framework state, never listed");
        inside.Success.Should().BeFalse("the staging folder lists like an absent one");
        blob.Success.Should().BeFalse("a staged blob is never read as content");
        blob.Error.Should().Contain("not found", "it is answered exactly as an absent file");
        visible.Success.Should().BeTrue();
        visible.Content.Should().Contain("admitted content", "an ordinary file beside it still reads");
    }
}
