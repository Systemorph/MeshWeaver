using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// Pins the observable, pooled <see cref="ContentCollection"/> surface — the contract the
/// FileBrowser "files disappeared" incident (2026-07-12) exposed as untested:
/// <list type="number">
///   <item><b>Upload → list → read</b> works end-to-end through the observable surface: a file
///     saved via <see cref="ContentCollection.SaveFile"/> is visible in the very next
///     <see cref="ContentCollection.GetCollectionItems"/> listing and readable via
///     <see cref="ContentCollection.GetContentBytes"/>.</item>
///   <item><b>The I/O leaves run OFF the subscriber's thread</b> — the provider is never entered
///     on the thread that subscribes (a Blazor circuit / hub action block stand-in). This is what
///     keeps a slow SMB mount from blocking the circuit.</item>
///   <item><b>The caller's <see cref="AccessContext"/> crosses the pool hop</b>: the write leaf
///     observes the identity that was ambient when <c>SaveFile</c> was CALLED, not an empty
///     pool-thread context.</item>
/// </list>
/// </summary>
public class ObservableCollectionSurfaceTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddContentCollections();

    private ContentCollection CreateCollection(out CapturingProvider provider)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Files", "ObservableSurface", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        provider = new CapturingProvider(new FileSystemStreamProvider(dir), GetHost());
        return new ContentCollection(
            new ContentCollectionConfig
            {
                Name = "content",
                SourceType = "FileSystem",
                IsEditable = true,
                BasePath = dir,
            },
            provider,
            GetHost());
    }

    [Fact]
    public async Task Upload_then_list_then_read_roundtrips_through_the_observable_surface()
    {
        var ct = TestContext.Current.CancellationToken;
        var collection = CreateCollection(out _);
        await collection.Initialize().FirstAsync().ToTask(ct);

        using (var stream = new MemoryStream("hello files"u8.ToArray()))
            await collection.SaveFile("/", "hello.txt", stream).ToTask(ct);

        // The very next listing must show the uploaded file — this is the FileBrowser
        // refresh-after-upload path that had no test when the Files tab "lost" uploads.
        var items = await collection.GetCollectionItems("/").ToList().FirstAsync().ToTask(ct);
        items.Should().Contain(i => i.Name == "hello.txt",
            "a file saved through the collection must be visible in the immediately following listing");

        var bytes = await collection.GetContentBytes("/hello.txt").FirstAsync().ToTask(ct);
        bytes.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(bytes!).Should().Be("hello files");

        var size = await collection.GetContentSize("/hello.txt").FirstAsync().ToTask(ct);
        size.Should().Be((long)bytes!.Length);

        await collection.DeleteFile("/hello.txt").ToTask(ct);
        var afterDelete = await collection.GetCollectionItems("/").ToList().FirstAsync().ToTask(ct);
        afterDelete.Should().NotContain(i => i.Name == "hello.txt");
    }

    [Fact]
    public async Task Provider_leaves_never_run_on_the_subscribing_thread()
    {
        var ct = TestContext.Current.CancellationToken;
        var collection = CreateCollection(out var provider);
        await collection.Initialize().FirstAsync().ToTask(ct);

        // Subscribe from a DEDICATED thread and BLOCK it until the pipeline completes. This pins
        // the decoupling property that broke in prod: a subscriber that blocks its own thread (a
        // Blazor circuit stand-in) must never deadlock the leaf, because the leaf runs on the
        // I/O pool. A dedicated thread's id can never coincide with a pool thread's while alive,
        // so the thread-inequality assertion is race-free (an awaiting xUnit thread would not be —
        // the ThreadPool may reuse it for the leaf).
        var subscriberThread = 0;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            subscriberThread = Environment.CurrentManagedThreadId;
            try
            {
                collection.SaveFile("/", "pooled.txt", () => new MemoryStream("pooled"u8.ToArray())).Wait();
                collection.GetCollectionItems("/").ToList().Wait();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(
            "a blocking subscriber must never deadlock the pooled leaves");
        error.Should().BeNull();

        provider.SaveThreadId.Should().NotBeNull();
        provider.SaveThreadId.Should().NotBe(subscriberThread,
            "the write leaf must run on the I/O pool, never the subscriber's thread (a blocked "
            + "circuit was the 'files disappeared' SignalR flapping)");
        provider.ListThreadId.Should().NotBeNull();
        provider.ListThreadId.Should().NotBe(subscriberThread,
            "the listing leaf must run on the I/O pool, never the subscriber's thread");
    }

    [Fact]
    public async Task SaveFile_carries_the_callers_AccessContext_across_the_pool_hop()
    {
        var ct = TestContext.Current.CancellationToken;
        var collection = CreateCollection(out var provider);
        await collection.Initialize().FirstAsync().ToTask(ct);

        var accessService = GetHost().ServiceProvider.GetRequiredService<AccessService>();
        var caller = new AccessContext { ObjectId = "test-user-object-id", Name = "Test User" };

        using (accessService.SwitchAccessContext(caller))
        using (var stream = new MemoryStream("who am I"u8.ToArray()))
            await collection.SaveFile("/", "identity.txt", stream).ToTask(ct);

        provider.SaveAccessContext.Should().NotBeNull(
            "the caller's AccessContext snapshot must be re-established inside the pool leaf — "
            + "the AsyncLocal is wiped by the pool hop otherwise");
        provider.SaveAccessContext!.ObjectId.Should().Be("test-user-object-id",
            "the write must stay attributed to the CALLING user, not the pool thread's ambient identity");
    }

    /// <summary>
    /// Delegates to a real provider while capturing, per operation, the managed thread id and the
    /// ambient <see cref="AccessContext"/> at the moment the leaf executes.
    /// </summary>
    private sealed class CapturingProvider(IStreamProvider inner, IMessageHub hub) : IStreamProvider
    {
        private AccessService? AccessService => hub.ServiceProvider.GetService<AccessService>();

        public int? SaveThreadId { get; private set; }
        public int? ListThreadId { get; private set; }
        public AccessContext? SaveAccessContext { get; private set; }

        public string ProviderType => inner.ProviderType;
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
        {
            ListThreadId = Environment.CurrentManagedThreadId;
            return inner.GetFolders(path, ct);
        }

        public IAsyncEnumerable<FileItem> GetFiles(string path, CancellationToken ct = default)
        {
            ListThreadId = Environment.CurrentManagedThreadId;
            return inner.GetFiles(path, ct);
        }

        public Task SaveFileAsync(string path, string fileName, Stream content, CancellationToken ct = default)
        {
            SaveThreadId = Environment.CurrentManagedThreadId;
            SaveAccessContext = AccessService?.Context;
            return inner.SaveFileAsync(path, fileName, content, ct);
        }

        public Task CreateFolderAsync(string folderPath) => inner.CreateFolderAsync(folderPath);
        public Task DeleteFolderAsync(string folderPath) => inner.DeleteFolderAsync(folderPath);
        public Task DeleteFileAsync(string filePath) => inner.DeleteFileAsync(filePath);
        public Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken ct = default)
            => inner.LoadAuthorsAsync(ct);
    }
}
