using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// DETERMINISTIC repro of the <c>CollectionNamedArea_UrlDecodesEncodedPathSegments</c> flake
/// (issue #978): the same markdown file is ingested from SEVERAL independent, legitimately
/// overlapping triggers, and before the fix the winner was whichever read COMPLETED last —
/// not whichever was triggered last.
///
/// <para><b>The defect.</b> A file written through the collection raises three ingests: the
/// watcher's <c>Created</c> event (inotify delivers it the instant the file is CREATED, while it
/// is still 0 bytes), <c>SaveFile</c>'s own read-your-writes ingest (after the write completed),
/// and the watcher's <c>Changed</c> event. Reads are deliberately share-tolerant — the write
/// takes <c>FileShare.Read|Delete</c> and the reads <c>FileShare.ReadWrite|Delete</c>, precisely
/// so a watcher read may overlap a write (see
/// <c>FileSystemStreamProvider.WriteStreamAsync</c>) — so the <c>Created</c>-triggered read is
/// ALLOWED to observe a half-written file. That is fine. What was not fine is that
/// <c>IngestContentFile</c> merged into <c>markdownStream</c> with no ordering rule at all, so
/// that torn read could land AFTER the complete one and stick. The collection then served a
/// <see cref="MarkdownElement"/> with empty <c>Content</c> and empty <c>PrerenderedHtml</c>
/// forever — the flake's exact CI signature, a last emission of
/// <c>MarkdownControl { Markdown = , Html = }</c> instead of a 30 s "not found".
///
/// <para><b>Why trigger order is the correct rule.</b> Every read observes the file at-or-after
/// its own trigger, so a LATER-triggered read can never carry OLDER content. Ordering by trigger
/// therefore needs no timestamps and no serialization of the reads themselves — it is the
/// article-level twin of <c>MonotonicWriteGuardStorageAdapter</c>'s per-path high-water mark.</para>
///
/// <para>Here the timing is removed entirely: the torn read is HELD until the complete article
/// has provably landed, then released. Pre-fix it overwrites the complete article; post-fix it is
/// dropped because a later-triggered ingest already applied.</para>
/// </summary>
public class ContentCollectionIngestOrderTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddContentCollections();

    private const string RealContent = "# Quarterly report with a space in its name";

    [Fact(Timeout = 60_000)]
    public async Task TornReadFromTheCreatedEvent_CannotOverwriteTheCompleteArticle()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(AppContext.BaseDirectory, "Files", "IngestOrder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var provider = new TornFirstReadProvider(new FileSystemStreamProvider(dir));
        using var collection = new ContentCollection(
            new ContentCollectionConfig
            {
                Name = "content",
                SourceType = "FileSystem",
                IsEditable = true,
                BasePath = dir,
            },
            provider,
            GetHost());
        await collection.Initialize().FirstAsync().ToTask(ct);

        // 1. The watcher's Created event, delivered while the file is still 0 bytes — the FIRST
        //    ingest triggered for this path. Its read is intercepted (empty content) and HELD.
        provider.FireWatcher("sub/hello.md");
        await provider.TornReadArrived.Should().Within(20.Seconds()).Emit();
        Output.WriteLine("[torn] the Created-triggered read reached the (empty) file and is held");

        // 2. The write completes and SaveFile's own read-your-writes ingest reads the COMPLETE
        //    file — a strictly LATER trigger, so its content must be the one that survives.
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(RealContent)))
            await collection.SaveFile("/sub", "hello.md", stream).ToTask(ct);

        var complete = await collection.GetMarkdown("sub/hello.md")
            .Should().Within(30.Seconds())
            .Match(x => x is MarkdownElement { Content: RealContent },
                "SaveFile's own ingest must publish the complete article");
        Output.WriteLine($"[complete] content='{((MarkdownElement)complete!).Content}'");

        // 3. Release the torn read. Pre-fix its empty parse lands LAST and wins; post-fix the
        //    claim taken at trigger time drops it, because a later-triggered ingest already
        //    applied for this key.
        provider.ReleaseTornRead();
        Output.WriteLine("[torn] released");

        // 4. Negative window — give the released ingest several of its own turns to do damage
        //    (the same shape StaleActivationSeedRollbackTest uses for the persistence sampler),
        //    then assert the collection still serves the complete article.
        await Observable.Timer(TimeSpan.FromSeconds(2)).Should().Within(20.Seconds()).Emit();

        var after = await collection.GetMarkdown("sub/hello.md")
            .Should().Within(20.Seconds()).Emit();
        after.Should().BeOfType<MarkdownElement>();
        ((MarkdownElement)after!).Content.Should().Be(RealContent,
            "a read triggered BEFORE the write (the watcher's Created event, which fires while the "
            + "file is still 0 bytes) must never overwrite the article published by a LATER-triggered "
            + "read — ingests apply in trigger order, not in completion order");
    }

    /// <summary>
    /// Delegates everything to a real provider, but (a) hands the monitor callback to the test so
    /// a <c>Created</c> event can be fired at an exact point, and (b) intercepts the FIRST
    /// <see cref="GetStreamWithMetadataAsync"/> call: it reports the file as EMPTY (the torn read
    /// of a just-created, not-yet-written file) and holds it on an <see cref="AsyncSubject{T}"/>
    /// gate until the test releases it — the same gate shape as
    /// <c>GatedReadStorageAdapter</c> in the Monolith test project.
    /// </summary>
    private sealed class TornFirstReadProvider(IStreamProvider inner) : IStreamProvider
    {
        private readonly AsyncSubject<Unit> gate = new();
        private readonly ReplaySubject<Unit> arrived = new(1);
        private int reads;
        private Action<string>? monitor;

        public IObservable<Unit> TornReadArrived => arrived.Take(1);

        public void FireWatcher(string path) => monitor?.Invoke(path);

        public void ReleaseTornRead()
        {
            gate.OnNext(Unit.Default);
            gate.OnCompleted();
        }

        public string ProviderType => inner.ProviderType;

        public IDisposable? AttachMonitor(Action<string> onChanged)
        {
            monitor = onChanged;
            return Disposable.Empty;
        }

        public async Task<(Stream? Stream, string Path, DateTime LastModified)> GetStreamWithMetadataAsync(
            string path, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                arrived.OnNext(Unit.Default);
                await gate.ToTask(ct);
                // The file existed but carried no bytes yet — exactly what a read racing
                // FileStream(FileMode.Create) ahead of CopyToAsync observes.
                return (new MemoryStream(), path, DateTime.UtcNow);
            }
            return await inner.GetStreamWithMetadataAsync(path, ct);
        }

        public Task<Stream?> GetStreamAsync(string reference, CancellationToken ct = default)
            => inner.GetStreamAsync(reference, ct);
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
