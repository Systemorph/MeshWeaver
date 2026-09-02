using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// 🚨 THE PRODUCER MUST NOT BUILD ONE DELIVERY WHOLE — issue #2885 (and its twin #3046).
///
/// <para><b>The incident.</b> A portal pod logged
/// <c>OrleansRoutingService: Failed to deliver to import/xDAfkqsVUE-OMBHb0mVtSg</c> with a
/// <c>System.OutOfMemoryException</c> raised at <c>SharedArrayPool.Rent</c> beneath
/// <c>Utf8JsonWriter.TranscodeAndWriteRawValue</c>, inside
/// <c>MessageDeliveryConverter.Write</c>. It recurred on 2026-09-02 against a second pod and a
/// second import hub, minutes after the <c>AgenticBusiness/_GitSync</c> node was written — i.e.
/// during a forced GitSync re-import.</para>
///
/// <para><b>The arithmetic that identifies the producer.</b> A GitSync import mirrors a Space's
/// git-committed <c>content/**</c> binaries with <c>SyncContentFilesRequest</c>, whose
/// <c>Files</c> carry the raw bytes INLINE. <c>ContentAssetMapper.ToContentSyncs</c> deliberately
/// emits ONE group per Space and <c>SyncContentFilesBuilder.Post</c> turned that group into ONE
/// message, so the delivery's size was the size of the Space's whole asset tree.
/// <c>AgenticBusiness/content/</c> is 28,484,421 bytes of course video across 10 files:
/// base64 (×4/3) ≈ 38 MB of JSON, held as a UTF-16 <c>RawJson.Content</c> string ≈ 76 MB, which
/// <c>TranscodeAndWriteRawValue</c> then transcodes by renting up to 3 bytes per char ≈ 114 MB —
/// once per hop, and the report about a failed hop carries the body again.
/// <c>AgenticEngineering/content/</c> is 106,070,300 bytes, whose base64 form exceeds Orleans'
/// 100 MiB <c>MaxMessageBodySize</c> outright: that Space's assets could not sync AT ALL.</para>
///
/// <para><b>Why no bound is the fix.</b> An <c>OutOfMemoryException</c> DURING serialization means
/// the ALLOCATION was the failure, so refusing the delivery afterwards is too late — the four
/// bounds already shipped for this family (#1890, #2897, #3018/#3032) all sit downstream of the
/// allocation, and the payload that killed the pod was in any case UNDER
/// <c>MaxMessageBodySize</c>. The only thing that removes the allocation is not making it: the
/// producer splits the write so that no single delivery ever carries the tree.</para>
///
/// <para><b>What is asserted here.</b> That a content sync larger than one delivery becomes
/// SEVERAL bounded deliveries whose union is exactly the supplied set, that exactly one of them
/// carries the authoritative mirror, and — the control — that a small sync is still ONE delivery
/// carrying no keep-set, byte-for-byte what it was before.</para>
/// </summary>
public class ContentSyncIsNeverBuiltWholeTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// The tighter of the two transport ceilings the mesh declares
    /// (<c>DeliveryPayloadBounds.MemoryStreamBlockBytes</c>), and the budget a content sync's
    /// packaged bytes are chunked against. Restated here as a literal so the test fails if the
    /// production constant is widened rather than silently following it.
    /// </summary>
    private const int PayloadBudgetBytes = 1 << 20;

    /// <summary>
    /// 300,000 raw bytes → 400,000 base64 chars, so two files fit one budget and three do not.
    /// Eight of them (2.4 MB raw, 3.2 MB packaged) therefore prove BATCHING, not merely
    /// one-file-per-delivery.
    /// </summary>
    private const int FileBytes = 300_000;

    private const int FileCount = 8;

    private const string NodePath = "host/1";

    /// <summary>Every <see cref="SyncContentFilesRequest"/> the target hub actually received.</summary>
    private readonly ConcurrentQueue<SyncContentFilesRequest> received = new();

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration configuration)
        => base.ConfigureMesh(configuration).WithTypes(
            typeof(SyncContentFilesRequest), typeof(InlineContentFile), typeof(ImportContentResponse));

    /// <summary>
    /// Stands in for the Space-root hub: records what it was handed and answers, so the producer's
    /// chunk sequence runs to completion and every delivery it built is observable. The real
    /// handler's behaviour across a split is asserted by
    /// <see cref="ContentSyncMirrorSurvivesTheSplitTest"/>.
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(SyncContentFilesRequest), typeof(InlineContentFile), typeof(ImportContentResponse))
            .WithHandler<SyncContentFilesRequest>((hub, delivery) =>
            {
                received.Enqueue(delivery.Message);
                hub.Post(ImportContentResponse.Ok(delivery.Message.Files.Count),
                    o => o.ResponseFor(delivery));
                return delivery.Processed();
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).WithTypes(
            typeof(SyncContentFilesRequest), typeof(InlineContentFile), typeof(ImportContentResponse));

    /// <summary>The base64 length of a file's bytes — what the JSON payload actually costs.</summary>
    private static long PackagedBytes(InlineContentFile file)
        => 4L * ((file.Content.Length + 2) / 3) + file.Path.Length;

    private static long PackagedBytes(IEnumerable<InlineContentFile> files)
        => files.Sum(PackagedBytes);

    private static InlineContentFile[] Files(int count, int bytes) =>
        Enumerable.Range(0, count)
            .Select(i => new InlineContentFile($"videos/asset-{i}.mp4", new byte[bytes]))
            .ToArray();

    /// <summary>
    /// 🚨 THE FACT. A sync whose bytes exceed one delivery's budget is written as SEVERAL
    /// deliveries, each within the budget plus at most one file, and their union is exactly the
    /// supplied set. Before the fix this is ONE delivery of ~3.2 MB — the shape that OOM'd
    /// production at 38 MB.
    /// </summary>
    [Fact]
    public async Task A_sync_larger_than_one_delivery_is_split()
    {
        var files = Files(FileCount, FileBytes);
        var whole = PackagedBytes(files);
        whole.Should().BeGreaterThan(PayloadBudgetBytes,
            "the fixture must be a set that CANNOT be carried by one delivery");

        var response = await GetClient().SyncContentFiles(NodePath)
            .To("content")
            .Add(files)
            .Mirror(true)
            .Post()
            .Timeout(TestTimeouts.Convergence)
            .FirstAsync();

        response.Success.Should().BeTrue(response.Error ?? string.Empty);
        response.FilesImported.Should().Be(FileCount, "every file must still be written");

        var deliveries = received.ToArray();
        deliveries.Length.Should().BeGreaterThan(1,
            "a set of {0} packaged bytes cannot be one delivery — that is the allocation that "
            + "threw OutOfMemoryException in production", whole);

        var largestFile = files.Max(PackagedBytes);
        foreach (var delivery in deliveries)
            PackagedBytes(delivery.Files).Should().BeLessThanOrEqualTo(PayloadBudgetBytes + largestFile,
                "no delivery may exceed the budget by more than the one file that cannot be split");

        deliveries.SelectMany(d => d.Files).Select(f => f.Path).ToArray()
            .Should().Equal(files.Select(f => f.Path).ToArray(),
                "the split must lose nothing, duplicate nothing and reorder nothing");
    }

    /// <summary>
    /// 🚨 THE PRUNE IS STILL ONE AUTHORITATIVE PASS. A mirror deletes what the source no longer
    /// carries, and a split must not turn that into several passes each measuring the collection
    /// against its OWN chunk — which would make every chunk prune the others' files. Exactly one
    /// delivery mirrors, and it names the FULL keep set rather than its own slice.
    /// </summary>
    [Fact]
    public async Task Exactly_one_delivery_carries_the_mirror_and_it_names_the_whole_set()
    {
        var files = Files(FileCount, FileBytes);

        await GetClient().SyncContentFiles(NodePath)
            .To("content", "TDD")
            .Add(files)
            .Mirror(true)
            .SourceOwned(["TDD/videos/gone.mp4"])
            .Post()
            .Timeout(TestTimeouts.Convergence)
            .FirstAsync();

        var deliveries = received.ToArray();
        deliveries.Length.Should().BeGreaterThan(1);

        var mirrors = deliveries.Where(d => d.Mirror).ToArray();
        mirrors.Should().ContainSingle("a split write still prunes exactly once");
        mirrors[0].SourceOwnedPaths.Should().Equal(["TDD/videos/gone.mp4"],
            "the #435 preserve-user-uploads set belongs to the pruning pass");
        mirrors[0].MirrorKeepPaths.Should().Equal(
            files.Select(f => $"TDD/{f.Path}").ToArray(),
            "the prune must be measured against the WHOLE set, not the chunk it travels with");

        deliveries.Where(d => !d.Mirror).Should().OnlyContain(d => d.SourceOwnedPaths == null,
            "an additive chunk prunes nothing, so it carries no prune inputs");
    }

    /// <summary>
    /// 🚨 THE CONTROL. A sync that fits stays exactly what it was: ONE delivery, mirroring
    /// directly against its own <c>Files</c>, with no keep set on the wire. The split must be
    /// invisible to every ordinary content sync.
    /// </summary>
    [Fact]
    public async Task A_sync_that_fits_is_still_one_delivery()
    {
        var files = Files(3, 1_024);
        PackagedBytes(files).Should().BeLessThan(PayloadBudgetBytes, "the control must fit");

        await GetClient().SyncContentFiles(NodePath)
            .To("content")
            .Add(files)
            .Mirror(true)
            .Post()
            .Timeout(TestTimeouts.Convergence)
            .FirstAsync();

        var deliveries = received.ToArray();
        deliveries.Should().ContainSingle("a sync that fits is not split");
        deliveries[0].Mirror.Should().BeTrue();
        deliveries[0].MirrorKeepPaths.Should().BeNull(
            "an unsplit write is measured against its own Files, exactly as before");
    }
}

/// <summary>
/// The split's semantic guard, driven through the REAL <c>SyncContentFilesRequest</c> handler and a
/// real file-system collection: after a write large enough to span several deliveries the folder
/// must mirror exactly the supplied set — every file written, the source's removed file pruned, and
/// a user upload the source never tracked PRESERVED (#435).
///
/// <para>This passes both before and after the fix, deliberately: it is what makes the split
/// safe to make, and it is the assertion that fails if the chunks are ever allowed to prune one
/// another.</para>
/// </summary>
public class ContentSyncMirrorSurvivesTheSplitTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const int FileBytes = 300_000;
    private const int FileCount = 8;
    private const string NodePath = "host/1";

    private readonly string contentPath = Path.Combine(
        AppContext.BaseDirectory, "Files", "SplitMirror", Guid.NewGuid().ToString("N")[..8]);

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration configuration)
        => base.ConfigureMesh(configuration).WithTypes(
            typeof(SyncContentFilesRequest), typeof(InlineContentFile), typeof(ImportContentResponse));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        Directory.CreateDirectory(contentPath);
        return base.ConfigureHost(configuration)
            .WithTypes(typeof(SyncContentFilesRequest), typeof(InlineContentFile), typeof(ImportContentResponse))
            .AddContentCollections()
            .AddFileSystemContentCollection("content", _ => contentPath);
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).WithTypes(
            typeof(SyncContentFilesRequest), typeof(InlineContentFile), typeof(ImportContentResponse));

    [Fact]
    public async Task A_split_mirror_writes_everything_prunes_the_stale_and_keeps_the_upload()
    {
        Directory.CreateDirectory(Path.Combine(contentPath, "videos"));
        // What the SOURCE carried last time and no longer carries — must be pruned.
        await File.WriteAllBytesAsync(Path.Combine(contentPath, "videos", "retired.mp4"), new byte[16]);
        // What a USER uploaded and the source never tracked — must survive (#435).
        await File.WriteAllBytesAsync(Path.Combine(contentPath, "videos", "user-upload.mp4"), new byte[16]);

        var files = Enumerable.Range(0, FileCount)
            .Select(i => new InlineContentFile($"videos/asset-{i}.mp4", new byte[FileBytes]))
            .ToArray();

        var response = await GetClient().SyncContentFiles(NodePath)
            .To("content")
            .Add(files)
            .Mirror(true)
            .SourceOwned(["videos/retired.mp4"])
            .Post()
            .Timeout(TestTimeouts.Convergence)
            .FirstAsync();

        response.Success.Should().BeTrue(response.Error ?? string.Empty);
        response.FilesImported.Should().Be(FileCount);

        var onDisk = Directory.GetFiles(Path.Combine(contentPath, "videos"))
            .Select(Path.GetFileName)
            .ToArray();

        onDisk.Should().Contain(files.Select(f => Path.GetFileName(f.Path)),
            "every supplied file must land, whichever delivery carried it");
        onDisk.Should().NotContain("retired.mp4",
            "a source-owned file the source no longer carries is pruned — once, by the mirror pass");
        onDisk.Should().Contain("user-upload.mp4",
            "a file the source never owned survives a mirror (#435) — and survives the split");
    }
}
