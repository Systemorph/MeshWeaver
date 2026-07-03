using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// Tests that content collections registered via AddContentCollection on node hubs
/// are properly discoverable and usable by the FileBrowser layout area.
/// Reproduces the prod issue where "content" collection is registered but uploads don't work.
/// </summary>
public class NodeHubContentCollectionTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly string _contentBasePath = Path.Combine(AppContext.BaseDirectory, "Files", "NodeContent");

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        // Simulate the distributed portal setup:
        // No global storage collection at mesh level.
        // Each node hub gets its own "content" collection.
        return base.ConfigureHost(configuration)
            .AddContentCollections();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        // Simulate a node hub with a direct "content" collection
        var nodePath = "TestOrg";
        var contentPath = Path.Combine(_contentBasePath, nodePath);
        Directory.CreateDirectory(contentPath);

        return base.ConfigureClient(configuration)
            // Layout areas ($Content, $FileBrowser, $Collection, and the collection-named area)
            .AddContentCollections()
            .AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = "content",
                SourceType = "FileSystem",
                IsEditable = true,
                ExposeInChildren = true,
                BasePath = contentPath,
                Settings = new Dictionary<string, string> { ["BasePath"] = contentPath }
            });
    }

    [Fact]
    public void NodeHub_Content_Collection_Is_Discoverable()
    {
        var hub = GetClient();
        var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();

        // GetCollectionConfig should find "content"
        var config = contentService.GetCollectionConfig("content");
        config.Should().NotBeNull("content collection should be registered on the node hub");
        config!.Name.Should().Be("content");
        config.IsEditable.Should().BeTrue("content collection should be editable");
    }

    [Fact]
    public void NodeHub_Content_Collection_Appears_In_GetAllCollectionConfigs()
    {
        var hub = GetClient();
        var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();

        var allConfigs = contentService.GetAllCollectionConfigs();
        allConfigs.Should().Contain(c => c.Name == "content",
            "content collection should appear in GetAllCollectionConfigs");
    }

    [Fact]
    public async Task NodeHub_Content_Collection_Can_Be_Resolved()
    {
        var hub = GetClient();
        var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();

        var collection = await contentService.GetCollectionAsync("content", TestContext.Current.CancellationToken);
        collection.Should().NotBeNull("content collection should resolve to a usable IContentCollection");
    }

    [Fact]
    public async Task NodeHub_Content_Collection_Supports_File_Operations()
    {
        var hub = GetClient();
        var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();

        var collection = await contentService.GetCollectionAsync("content", TestContext.Current.CancellationToken);
        collection.Should().NotBeNull();

        // Create a test file
        var testContent = "Hello from unit test"u8.ToArray();
        using var stream = new MemoryStream(testContent);
        await collection!.SaveFileAsync("/", "test.txt", stream);

        // Verify it was saved
        var ct = TestContext.Current.CancellationToken;
        var items = await collection.GetCollectionItems("/", ct).ToListAsync(ct);
        items.Should().Contain(i => i.Name == "test.txt");

        // Clean up
        await collection.DeleteFileAsync("/test.txt");
    }

    [Fact]
    public void Hidden_Storage_Collection_Not_In_GetAllCollectionConfigs()
    {
        // Simulate the scenario where storage is registered but hidden
        var hub = GetClient();

        // Register a hidden storage collection (ExposeInChildren = false, the default)
        var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();
        contentService.AddConfiguration(new ContentCollectionConfig
        {
            Name = "storage",
            SourceType = "FileSystem",
            IsEditable = true,
            // ExposeInChildren defaults to false — collection is hidden from children.
            BasePath = _contentBasePath
        });

        var allConfigs = contentService.GetAllCollectionConfigs();

        // "storage" should NOT appear (ExposeInChildren = false, the default)
        allConfigs.Should().NotContain(c => c.Name == "storage",
            "hidden storage collection should not appear in GetAllCollectionConfigs");

        // "content" should still appear
        allConfigs.Should().Contain(c => c.Name == "content",
            "content collection should still appear");

        // But GetCollectionConfig should still find "storage" (for internal use)
        var storageConfig = contentService.GetCollectionConfig("storage");
        storageConfig.Should().NotBeNull("storage should still be resolvable directly");
    }

    /// <summary>
    /// The collection-named layout area serves the collection's OWN URL space —
    /// <c>/{node}/{collection}/{path…}</c>, where the segment is the MOUNTED collection name
    /// (a collection can be mounted under any name; nothing assumes "content"): a folder path
    /// renders the file browser scoped to that folder with the URL base reflecting the
    /// collection name, a file path renders the file's content.
    /// </summary>
    [Fact]
    public async Task CollectionNamedArea_RendersBrowserForFolder_AndContentForFile()
    {
        var client = GetClient();
        var contentService = client.ServiceProvider.GetRequiredService<IContentService>();
        var ct = TestContext.Current.CancellationToken;
        var collection = await contentService.GetCollectionAsync("content", ct);
        collection.Should().NotBeNull();

        // A nested file so folder and file rendering are distinguishable.
        var bytes = "# Hello from the collection area"u8.ToArray();
        using (var stream = new MemoryStream(bytes))
            await collection!.SaveFileAsync("/sub", "hello.md", stream);

        try
        {
            // Subscribe from the HOST hub — a hub cannot GetRemoteStream its own address.
            var workspace = GetHost().GetWorkspace();

            // Folder → the file browser scoped to /sub, mirroring the collection-named URL space.
            var folderControl = await workspace
                .GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
                    client.Address, new LayoutAreaReference("content") { Id = "sub" })
                .GetControlStream("content")
                .Should().Within(30.Seconds()).Match(c => c != null);
            var folderJson = folderControl!.ToString()!;
            folderJson.Should().Contain("FileBrowser", "a folder path renders the browser");
            folderJson.Should().Contain("/sub", "the browser is scoped to the folder");
            folderJson.Should().Contain($"/{client.Address}/content",
                "navigation mirrors into the collection-named URL space");

            // File → the file's rendered content, not a browser.
            var fileControl = await workspace
                .GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
                    client.Address, new LayoutAreaReference("content") { Id = "sub/hello.md" })
                .GetControlStream("content")
                .Should().Within(30.Seconds())
                .Match(c => c != null && c.ToString()!.Contains("Hello from the collection area"));
            fileControl!.ToString().Should().NotContain("FileBrowser",
                "a file path renders content, not a browser");
        }
        finally
        {
            await collection!.DeleteFileAsync("/sub/hello.md");
        }
    }
}
