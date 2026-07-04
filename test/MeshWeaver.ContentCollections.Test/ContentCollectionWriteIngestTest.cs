using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// Pins the root cause of the CI-only <c>CollectionNamedArea_RendersBrowserForFolder_AndContentForFile</c>
/// flake (a 30s timeout on the FILE-content render while the folder render passed): a markdown file
/// written THROUGH the collection must be readable immediately, WITHOUT depending on the file-system
/// watcher.
///
/// <para>The watcher is the only thing that fed a post-init write into <c>markdownStream</c> (the
/// content read path), but on Linux inotify drops the event for a file created in a just-made
/// subdirectory — so the content render stayed "not found" until re-init. macOS FSEvents never
/// misses, so it only ever flaked on the Linux runner and never reproduced locally.</para>
///
/// <para>Here the watcher is NEUTERED (<see cref="NoMonitorProvider"/> never fires
/// <c>onChanged</c>), so the article can only reach <c>markdownStream</c> via
/// <see cref="ContentCollection.SaveFileAsync"/>'s proactive ingest. This makes the guarantee
/// deterministic and OS-independent: without the ingest the read never resolves (fails fast at the
/// 10s timeout instead of flaking); with it the article is readable at once.</para>
/// </summary>
public class ContentCollectionWriteIngestTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddContentCollections();

    [Fact]
    public async Task SaveFileAsync_ingests_the_article_without_the_watcher()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(AppContext.BaseDirectory, "Files", "WriteIngest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var collection = new ContentCollection(
            new ContentCollectionConfig
            {
                Name = "content",
                SourceType = "FileSystem",
                IsEditable = true,
                BasePath = dir,
            },
            new NoMonitorProvider(new FileSystemStreamProvider(dir)),
            GetHost());
        await collection.InitializeAsync(ct);

        // Write into a NESTED, freshly-created subdirectory — the exact shape whose inotify event
        // the Linux runner dropped. With the watcher neutered this can ONLY resolve via the
        // collection's own proactive ingest on write.
        using (var stream = new MemoryStream("# Hello ingest"u8.ToArray()))
            await collection.SaveFileAsync("/sub", "hello.md", stream);

        // GetMarkdown is the SAME content read the file render uses (ContentLayoutArea.RenderFile).
        // Without the fix this stays null (the watcher never fires) and the wait times out — the
        // deterministic stand-in for the 30s CI flake.
        var article = await collection.GetMarkdown("sub/hello.md")
            .Where(x => x is not null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .ToTask(ct);

        article.Should().NotBeNull(
            "SaveFileAsync must ingest its own write into markdownStream — the content render must "
            + "not wait on the file-system watcher (which drops the event on Linux)");
    }

    /// <summary>
    /// Delegates every operation to a real provider but NEVER fires change notifications
    /// (<see cref="AttachMonitor"/> returns an inert handle), so a read after a write can only
    /// succeed through the collection's proactive ingest — never the watcher.
    /// </summary>
    private sealed class NoMonitorProvider(IStreamProvider inner) : IStreamProvider
    {
        public string ProviderType => inner.ProviderType;

        // The whole point: the monitor is inert.
        public IDisposable? AttachMonitor(Action<string> onChanged) => Disposable.Empty;

        public Task<Stream?> GetStreamAsync(string reference, CancellationToken ct = default)
            => inner.GetStreamAsync(reference, ct);
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
        public Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken ct = default)
            => inner.LoadAuthorsAsync(ct);
    }
}
