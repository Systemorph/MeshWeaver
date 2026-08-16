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
/// DETERMINISTIC repro of the #1692 recurrence of the CollectionNamedArea empty-render flake —
/// the residual hole #978 left open.
///
/// <para><b>The defect.</b> #978 established the trigger-order rule: every ingest of a file
/// claims a monotonic sequence at TRIGGER time, and an ingest may only merge if no
/// later-triggered ingest of the same file has already landed (a read observes the file
/// at-or-after its own trigger, so a later trigger can never carry older content). But the shipped
/// code CHECKED that claim on the pool read-completion thread and then POSTED the merge to the
/// stream's sync hub as a separate step. Claim and apply were not atomic: the watcher's
/// <c>Created</c>-event ingest (triggered while the just-created file is still 0 bytes, so its
/// share-tolerant read legally parses an EMPTY article) could pass its claim, then be overtaken —
/// before its post enqueued — by SaveFile's later-triggered read-your-writes ingest claiming and
/// posting the complete article. The hub then applied complete-then-torn: apply order inverted
/// claim order, the empty article landed last and stuck, and (Linux inotify dropping events for
/// files written into a just-created subdirectory) no repair event ever came. The collection
/// served <c>MarkdownControl { Markdown = , Html = }</c> forever — the exact CI signature of
/// runs 31972489250 (#1692) and the original #978 failure.</para>
///
/// <para><b>The fix.</b> <see cref="ContentCollection.MergeIngestedArticle"/> evaluates
/// <c>ClaimIngest</c> INSIDE the update transform, which the sync hub invokes strictly serially —
/// claim order IS apply order by construction; a superseded ingest's transform returns
/// <c>null</c> and the stream skips it as a no-op.</para>
///
/// <para><b>The pin.</b> Timing is removed entirely: a collection subclass HOLDS the torn ingest
/// at the exact boundary the defect lives on — after its read completed and parsed (so, pre-fix,
/// after its claim had already passed), before its merge is posted. The complete article is then
/// written and provably applied, and only afterwards is the held merge released. Pre-fix the
/// released post applies unconditionally and the empty article overwrites the complete one
/// (guaranteed loss); post-fix the claim — evaluated at apply time under the stream's
/// serialization — drops it.</para>
/// </summary>
public class ContentCollectionClaimApplyRaceTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddContentCollections();

    private const string RealContent = "# Hello from the collection area";

    [Fact(Timeout = 60_000)]
    public async Task TornIngestOvertakenBetweenClaimAndPost_CannotOverwriteTheLaterTriggeredArticle()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(AppContext.BaseDirectory, "Files", "ClaimApplyRace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var provider = new TornFirstReadProvider(new FileSystemStreamProvider(dir));
        using var collection = new HoldFirstMergeCollection(
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
        //    ingest triggered for this path. Its (empty) read completes immediately, and the
        //    collection subclass holds it right at the merge boundary: post-parse — and, pre-fix,
        //    post-CLAIM — but pre-post. This is the overtaking window: a thread paused here while
        //    a later-triggered ingest claims, posts, and applies.
        provider.FireWatcher("sub/hello.md");
        await collection.HeldMergeArrived.Should().Within(20.Seconds()).Emit();
        Output.WriteLine("[torn] the Created-triggered ingest parsed the empty file and is held at the merge boundary");

        // 2. The write completes and SaveFile's own read-your-writes ingest reads the COMPLETE
        //    file — a strictly LATER trigger — and provably applies it.
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(RealContent)))
            await collection.SaveFile("/sub", "hello.md", stream).ToTask(ct);

        var complete = await collection.GetMarkdown("sub/hello.md")
            .Should().Within(30.Seconds())
            .Match(x => x is MarkdownElement { Content: RealContent },
                "SaveFile's own ingest must publish the complete article");
        Output.WriteLine($"[complete] content='{((MarkdownElement)complete!).Content}'");

        // 3. Release the held torn merge — its post now enqueues AFTER the complete article
        //    applied, even though its trigger (and, pre-fix, its claim) came FIRST. Pre-fix it
        //    applies unconditionally and the empty article wins; post-fix the claim is evaluated
        //    at apply time under the stream's serialization and drops it.
        collection.ReleaseHeldMerge();
        Output.WriteLine("[torn] released");

        // 4. Negative window — give the released merge several of its own turns to do damage,
        //    then assert the collection still serves the complete article.
        await Observable.Timer(TimeSpan.FromSeconds(2)).Should().Within(20.Seconds()).Emit();

        var after = await collection.GetMarkdown("sub/hello.md")
            .Should().Within(20.Seconds()).Emit();
        after.Should().BeOfType<MarkdownElement>();
        ((MarkdownElement)after!).Content.Should().Be(RealContent,
            "an ingest whose merge post is overtaken between its trigger-order claim and its "
            + "enqueue must never overwrite the article of a later-triggered ingest that already "
            + "applied — the claim must be evaluated atomically with the apply, under the "
            + "stream's serialization");
    }

    /// <summary>
    /// Exposes the exact boundary the #1692 defect lives on: the FIRST completed ingest (the
    /// torn Created-event read) is captured after its parse — pre-fix, after its claim had
    /// already passed — and before its merge posts to the stream. <see cref="ReleaseHeldMerge"/>
    /// then issues that merge after a later-triggered ingest has provably applied, which is the
    /// overtaking the check-then-act window allowed a preempted pool thread to suffer for real.
    /// No thread is blocked: the hold is a capture-and-return, the release replays the base merge.
    /// </summary>
    private sealed class HoldFirstMergeCollection(
        ContentCollectionConfig config, IStreamProvider provider, IMessageHub hub)
        : ContentCollection(config, provider, hub)
    {
        private readonly ReplaySubject<Unit> heldArrived = new(1);
        private (MarkdownElement Article, string Key, long Sequence, AccessContext? Caller)? held;
        private int merges;

        public IObservable<Unit> HeldMergeArrived => heldArrived.Take(1);

        protected override void MergeIngestedArticle(
            MarkdownElement article, string key, long sequence, AccessContext? caller)
        {
            if (Interlocked.Increment(ref merges) == 1)
            {
                held = (article, key, sequence, caller);
                heldArrived.OnNext(Unit.Default);
                return;
            }
            base.MergeIngestedArticle(article, key, sequence, caller);
        }

        public void ReleaseHeldMerge()
        {
            var (article, key, sequence, caller) = held!.Value;
            base.MergeIngestedArticle(article, key, sequence, caller);
        }
    }

    /// <summary>
    /// Delegates everything to a real provider, but (a) hands the monitor callback to the test so
    /// a <c>Created</c> event can be fired at an exact point, and (b) intercepts the FIRST
    /// <see cref="GetStreamWithMetadataAsync"/> call to report the file as EMPTY — the torn read
    /// of a just-created, not-yet-written file (the write is share-tolerant by design, see
    /// <c>FileSystemStreamProvider.WriteStreamAsync</c>). Unlike
    /// <c>ContentCollectionIngestOrderTest</c>'s provider there is no gate on the read: #1692's
    /// hole is AFTER the read completes, so the torn read finishes immediately and the hold
    /// happens at the collection's merge boundary instead.
    /// </summary>
    private sealed class TornFirstReadProvider(IStreamProvider inner) : IStreamProvider
    {
        private int reads;
        private Action<string>? monitor;

        public void FireWatcher(string path) => monitor?.Invoke(path);

        public string ProviderType => inner.ProviderType;

        public IDisposable? AttachMonitor(Action<string> onChanged)
        {
            monitor = onChanged;
            return Disposable.Empty;
        }

        public Task<(Stream? Stream, string Path, DateTime LastModified)> GetStreamWithMetadataAsync(
            string path, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref reads) == 1)
                // The file existed but carried no bytes yet — exactly what a read racing
                // FileStream(FileMode.Create) ahead of CopyToAsync observes.
                return Task.FromResult<(Stream? Stream, string Path, DateTime LastModified)>(
                    (new MemoryStream(), path, DateTime.UtcNow));
            return inner.GetStreamWithMetadataAsync(path, ct);
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
