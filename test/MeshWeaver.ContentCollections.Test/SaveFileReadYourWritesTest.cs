using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// Regression guard for the CI flake in
/// <c>NodeHubContentCollectionTest.CollectionNamedArea_RendersBrowserForFolder_AndContentForFile</c>:
/// the file-content render reduces the collection's markdown stream, which used to be populated ONLY
/// by the <c>FileSystemWatcher</c>. The watcher's events are asynchronous and lossy (coalesced, its
/// OS buffer overflows under load), so a save→render could hang until an event that arrived late — or
/// never — → a 30s render timeout (<c>ObservableAssertionException : did not emit within 30s</c>).
/// <para>
/// This runs the SAME save→render, but with the watcher turned OFF (<c>DisableFileWatcher</c>), so the
/// file can reach the stream ONLY through <see cref="ContentCollection.SaveFileAsync"/>'s own merge
/// (read-your-writes). With the fix it renders deterministically; if the merge regresses, this test
/// deterministically times out instead of silently re-introducing a load-dependent flake elsewhere.
/// </para>
/// </summary>
public class SaveFileReadYourWritesTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly string _contentPath = Path.Combine(
        AppContext.BaseDirectory, "Files", "NoWatchContent", Guid.NewGuid().ToString("N"));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddContentCollections();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        Directory.CreateDirectory(_contentPath);
        return base.ConfigureClient(configuration)
            .AddContentCollections()
            .AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = "content",
                SourceType = "FileSystem",
                IsEditable = true,
                ExposeInChildren = true,
                DisableFileWatcher = true,   // ← the point: no watcher; only the save-time merge can deliver
                BasePath = _contentPath,
                Settings = new Dictionary<string, string> { ["BasePath"] = _contentPath }
            });
    }

    [HubFact]
    public async Task File_Rendered_Right_After_Save_Without_Watcher()
    {
        var client = GetClient();
        var contentService = client.ServiceProvider.GetRequiredService<IContentService>();
        var ct = TestContext.Current.CancellationToken;
        var collection = await contentService.GetCollectionAsync("content", ct);
        collection.Should().NotBeNull();

        using (var stream = new MemoryStream("# Read your writes — no watcher"u8.ToArray()))
            await collection!.SaveFileAsync("/notes", "hello.md", stream);

        // Render the just-saved file. With the watcher off, the ONLY way the content reaches the
        // markdown stream (and thus this render) is SaveFileAsync's own merge.
        var fileControl = await GetHost().GetWorkspace()
            .GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
                client.Address, new LayoutAreaReference("content") { Id = "notes/hello.md" })
            .GetControlStream("content")
            .Should().Within(30.Seconds())
            .Match(c => c != null && c.ToString()!.Contains("Read your writes"));

        fileControl!.ToString().Should().Contain("Read your writes");
    }
}
