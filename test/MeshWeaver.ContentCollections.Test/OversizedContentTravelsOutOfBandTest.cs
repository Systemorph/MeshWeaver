using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// 🚨 <b>A FILE OVER THE PER-DELIVERY BUDGET MUST NOT RIDE THE MESSAGE — issue #3233.</b>
///
/// <para><b>The residual #3101 left open.</b> #2885 partitions a content sync so a delivery is
/// <c>≤ budget + largest single file</c>, which fixed every Space whose AGGREGATE was the problem.
/// But a file is the atom the receiving handler writes and is never split, so a file whose packaged
/// (base64) cost alone exceeds the budget travelled whole — and was refused wherever the Orleans
/// transport was in the path. Measured on <c>MeshWeaver.Education@f7ae723</c> (2026-09-04):
/// <b>25 files across all 7 Spaces</b>, the largest packaging to 13,188,871 bytes against a
/// 1,048,576-byte budget. The axis is "has a video", not "is large" — the smallest Space in that
/// repo carries the second-largest single file.</para>
///
/// <para><b>The budget is not a knob.</b> <c>ContentDeliveryBudget.BudgetBytes</c> is
/// <c>DeliveryPayloadBounds.MemoryStreamBlockBytes</c>, Orleans' memory-stream block size,
/// hard-coded in <c>MemoryAdapterFactory</c>. Raising it is not available and would be the move
/// <c>Doc/Architecture/OversizedDeliveryRefusal</c> exists to forbid. So the bytes take a different
/// road: they are written into the DESTINATION collection's reserved staging folder before the
/// request is posted, and the delivery carries a content-addressed
/// <see cref="StagedContentFile"/> handle.</para>
///
/// <para><b>What is asserted here</b>, against the REAL <c>SyncContentFilesRequest</c> handler and a
/// real file-system collection: that the over-budget file arrives byte-for-byte; that its bytes are
/// on NO delivery, so every delivery is inside the budget outright rather than "budget plus one
/// file"; that the mirror keeps it and never prunes the staging folder; that a re-run duplicates
/// nothing and leaves no residue.</para>
///
/// <para>Design: <c>Doc/Architecture/OutOfBandContentTransfer</c>.</para>
/// </summary>
public class OversizedContentTravelsOutOfBandTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// The tighter of the two transport ceilings the mesh declares
    /// (<c>DeliveryPayloadBounds.MemoryStreamBlockBytes</c>). Restated as a literal so this test
    /// fails if the production constant is ever widened rather than silently following it.
    /// </summary>
    private const int PayloadBudgetBytes = 1 << 20;

    /// <summary>2,000,000 raw bytes → 2,666,668 base64 chars: over the budget on its own, whatever
    /// the rest of the set looks like. This is the file class that cannot sync today.</summary>
    private const int OversizedBytes = 2_000_000;

    /// <summary>Comfortably inside the budget — the control files that still travel inline.</summary>
    private const int SmallBytes = 64_000;

    private const string NodePath = "host/1";
    private const string OversizedPath = "videos/module1-intro.mp4";

    private readonly string contentPath = Path.Combine(
        AppContext.BaseDirectory, "Files", "OutOfBand", Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Every <see cref="SyncContentFilesRequest"/> the target hub actually received.</summary>
    private readonly ConcurrentQueue<SyncContentFilesRequest> received = new();

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration configuration)
        => base.ConfigureMesh(configuration).WithTypes(
            typeof(SyncContentFilesRequest), typeof(InlineContentFile),
            typeof(StagedContentFile), typeof(ImportContentResponse));

    /// <summary>
    /// The Space-root hub: the REAL content-import handlers over a real file-system collection,
    /// plus a passive spy that records what each delivery carried and hands the delivery straight
    /// on — it processes nothing, so the production handler does all the work.
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        Directory.CreateDirectory(contentPath);
        return base.ConfigureHost(configuration)
            .WithTypes(typeof(SyncContentFilesRequest), typeof(InlineContentFile),
                typeof(StagedContentFile), typeof(ImportContentResponse))
            .WithHandler<SyncContentFilesRequest>((_, delivery) =>
            {
                received.Enqueue(delivery.Message);
                return delivery;
            })
            .AddContentCollections()
            .AddFileSystemContentCollection("content", _ => contentPath);
    }

    /// <summary>
    /// The producing hub. It needs the content infrastructure because THAT is what makes the
    /// out-of-band write possible: the producer resolves the destination collection's CONFIG from
    /// the owning node's hub and writes the bytes through it — only the config crosses the mesh.
    /// </summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(SyncContentFilesRequest), typeof(InlineContentFile),
                typeof(StagedContentFile), typeof(ImportContentResponse))
            .AddContentCollections();

    /// <summary>The base64 length of a file's bytes — what the JSON payload actually costs.</summary>
    private static long PackagedBytes(InlineContentFile file)
        => 4L * ((file.Content.Length + 2) / 3) + file.Path.Length;

    private static byte[] Pattern(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)((i * 31 + seed) % 251);
        return bytes;
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private InlineContentFile[] Fixture() =>
    [
        new("videos/poster-0.png", Pattern(SmallBytes, 1)),
        new("videos/poster-1.png", Pattern(SmallBytes, 2)),
        new(OversizedPath, Pattern(OversizedBytes, 3)),
        new("videos/poster-2.png", Pattern(SmallBytes, 4)),
    ];

    private IObservable<ImportContentResponse> Sync(
        IMessageHub client, IReadOnlyList<InlineContentFile> files, IReadOnlyList<string>? sourceOwned = null)
        => client.SyncContentFiles(NodePath)
            .To("content")
            .Add(files)
            .Mirror(true)
            .SourceOwned(sourceOwned)
            .Post();

    private string[] StagingResidue()
    {
        var staging = Path.Combine(contentPath, ContentStaging.Folder);
        return Directory.Exists(staging)
            ? Directory.GetFiles(staging).Select(f => Path.GetFileName(f)).ToArray()
            : [];
    }

    /// <summary>
    /// 🚨 THE FACT. A file whose packaged cost alone exceeds the budget arrives INTACT, and its
    /// bytes are on no delivery: they went into the collection's staging folder and the delivery
    /// carried a content-addressed handle. Before the fix the file rides inline, so
    /// <c>StagedFiles</c> is empty and one delivery weighs 2.6 MB — the payload the transport
    /// refuses.
    /// </summary>
    [Fact]
    public async Task A_file_over_the_budget_travels_out_of_band_and_arrives_intact()
    {
        var files = Fixture();
        var oversized = files.Single(f => f.Path == OversizedPath);
        PackagedBytes(oversized).Should().BeGreaterThan(PayloadBudgetBytes,
            "the fixture must contain a file that CANNOT fit any delivery — that is the defect");

        var response = await Sync(GetClient(), files)
            .Timeout(TestTimeouts.Convergence)
            .FirstAsync();

        response.Success.Should().BeTrue(response.Error ?? string.Empty);
        response.FilesImported.Should().Be(files.Length, "every file must still be written");

        // The bytes arrived, unaltered.
        var landed = await File.ReadAllBytesAsync(Path.Combine(contentPath, "videos", "module1-intro.mp4"));
        Sha256Hex(landed).Should().Be(Sha256Hex(oversized.Content),
            "an out-of-band transfer that changes the bytes is worse than one that fails");

        var deliveries = received.ToArray();
        deliveries.Should().NotBeEmpty();

        deliveries.SelectMany(d => d.Files).Select(f => f.Path)
            .Should().NotContain(OversizedPath,
                "the over-budget file's BYTES must be on no message — that is the whole point");

        var staged = deliveries.SelectMany(d => d.StagedFiles ?? []).ToArray();
        staged.Should().ContainSingle("the one over-budget file is the one transferred out of band");
        staged[0].Path.Should().Be(OversizedPath);
        staged[0].Length.Should().Be(OversizedBytes);
        staged[0].Handle.Should().Be(Sha256Hex(oversized.Content),
            "the handle is CONTENT-addressed — that is what makes a re-run a no-op rather than a copy");

        foreach (var delivery in deliveries)
            (delivery.Files.Sum(PackagedBytes) + (delivery.StagedFiles?.Count ?? 0) * 256L)
                .Should().BeLessThanOrEqualTo(PayloadBudgetBytes,
                    "with the oversized file out of band, no delivery needs the 'plus one file' "
                    + "allowance any more — every delivery fits the budget outright");

        StagingResidue().Should().BeEmpty(
            "the producer owns the staged bytes and reclaims them when the sequence terminates — "
            + "an orphan is a leak on the content share");
    }

    /// <summary>
    /// 🚨 IDEMPOTENCE. Running the same sync twice writes the same file at the same path and leaves
    /// nothing behind — no second copy of the asset, no second staged blob. The handle is the SHA-256
    /// of the bytes, so the second run addresses exactly what the first one did.
    /// </summary>
    [Fact]
    public async Task A_rerun_duplicates_nothing_and_leaves_no_residue()
    {
        var files = Fixture();
        var client = GetClient();

        var first = await Sync(client, files).Timeout(TestTimeouts.Convergence).FirstAsync();
        first.Success.Should().BeTrue(first.Error ?? string.Empty);

        var owned = files.Select(f => f.Path).ToArray();
        var second = await Sync(client, files, owned).Timeout(TestTimeouts.Convergence).FirstAsync();
        second.Success.Should().BeTrue(second.Error ?? string.Empty);

        var onDisk = Directory.GetFiles(Path.Combine(contentPath, "videos"))
            .Select(f => Path.GetFileName(f)).ToArray();
        onDisk.Should().HaveCount(files.Length,
            "a second sync of the same set is the same folder, not a folder with copies in it");
        onDisk.Should().Contain(files.Select(f => Path.GetFileName(f.Path)).ToArray());

        var landed = await File.ReadAllBytesAsync(Path.Combine(contentPath, "videos", "module1-intro.mp4"));
        Sha256Hex(landed).Should().Be(Sha256Hex(files.Single(f => f.Path == OversizedPath).Content));

        var handles = received.ToArray().SelectMany(d => d.StagedFiles ?? []).Select(s => s.Handle).ToArray();
        handles.Should().HaveCount(2, "each run transfers the one over-budget file out of band");
        handles.Distinct().Should().ContainSingle(
            "the handle is the CONTENT hash, so both runs address the same blob — which is exactly "
            + "why the second run writes no second copy of anything");

        StagingResidue().Should().BeEmpty("neither run may leave a staged blob behind");
    }

    /// <summary>
    /// 🚨 THE MIRROR STILL MIRRORS — and it must not eat the transfer. A staged file is not in its
    /// delivery's <c>Files</c>, so without the full keep set the ONE prune pass (which rides the
    /// FIRST delivery) would delete every out-of-band asset it had just received; and the staging
    /// folder itself is framework state the prune must never touch, because it still holds the blobs
    /// the following deliveries have to read.
    /// </summary>
    [Fact]
    public async Task The_mirror_keeps_the_out_of_band_file_prunes_the_stale_and_preserves_an_upload()
    {
        Directory.CreateDirectory(Path.Combine(contentPath, "videos"));
        // What the SOURCE carried last time and no longer carries — must be pruned.
        await File.WriteAllBytesAsync(Path.Combine(contentPath, "videos", "retired.mp4"), new byte[16]);
        // What a USER uploaded and the source never tracked — must survive (#435).
        await File.WriteAllBytesAsync(Path.Combine(contentPath, "videos", "user-upload.mp4"), new byte[16]);

        var files = Fixture();
        var response = await Sync(GetClient(), files, ["videos/retired.mp4"])
            .Timeout(TestTimeouts.Convergence)
            .FirstAsync();

        response.Success.Should().BeTrue(response.Error ?? string.Empty);

        var onDisk = Directory.GetFiles(Path.Combine(contentPath, "videos"))
            .Select(f => Path.GetFileName(f)).ToArray();
        onDisk.Should().Contain("module1-intro.mp4",
            "the out-of-band file must survive the mirror that runs in the same operation");
        onDisk.Should().NotContain("retired.mp4",
            "a source-owned file the source no longer carries is still pruned — once");
        onDisk.Should().Contain("user-upload.mp4",
            "a file the source never owned survives a mirror (#435)");

        var mirrors = received.ToArray().Where(d => d.Mirror).ToArray();
        mirrors.Should().ContainSingle("the prune is still ONE authoritative pass");
        mirrors[0].MirrorKeepPaths.Should().Contain(OversizedPath,
            "a staged file is not in Files, so it must be named in the keep set or the prune deletes it");

        StagingResidue().Should().BeEmpty();
    }
}

/// <summary>
/// 🚨 <b>A TRANSFER THAT FAILS STILL SAYS WHY — issue #3233 must not undo issue #3101.</b>
///
/// <para>#3101's whole contribution was that a refused content sync is OBSERVABLE: the reason, the
/// size and the limit are recorded, and each Space carries an <c>_Activity/content-sync</c> ledger.
/// An out-of-band transfer that quietly reported success — or quietly reported "zero files", the
/// original defect — would give that back. So both failure modes are pinned here: staging that
/// cannot run at all, and a handle the receiver cannot resolve.</para>
/// </summary>
public class RefusedOutOfBandContentStillSaysWhyTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const int OversizedBytes = 2_000_000;
    private const string NodePath = "host/1";

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration configuration)
        => base.ConfigureMesh(configuration).WithTypes(
            typeof(SyncContentFilesRequest), typeof(InlineContentFile),
            typeof(StagedContentFile), typeof(ImportContentResponse));

    /// <summary>
    /// The content infrastructure WITHOUT the <c>content</c> collection: the node answers the
    /// config read (so nothing hangs) with "no such collection", which is exactly the shape of a
    /// deployment where the destination store cannot be reached from the producer.
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(SyncContentFilesRequest), typeof(InlineContentFile),
                typeof(StagedContentFile), typeof(ImportContentResponse))
            .AddContentCollections();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(SyncContentFilesRequest), typeof(InlineContentFile),
                typeof(StagedContentFile), typeof(ImportContentResponse))
            .AddContentCollections();

    /// <summary>
    /// 🚨 Staging that cannot run falls back to the LOUD path, never a quiet one: the files travel
    /// inline exactly as they did before #3233, the sync fails for its own reason, and the answer
    /// carries BOTH the #3101 budget measurement (naming the file, its packaged size and the limit)
    /// AND why the out-of-band road was unavailable.
    /// </summary>
    [Fact]
    public async Task Staging_that_cannot_run_falls_back_inline_and_the_failure_names_both_halves()
    {
        var response = await GetClient().SyncContentFiles(NodePath)
            .To("content")
            .Add("videos/module1-intro.mp4", new byte[OversizedBytes])
            .Mirror(true)
            .Post()
            .Timeout(TestTimeouts.Convergence)
            .FirstAsync();

        response.Success.Should().BeFalse("the destination collection does not exist on that node");
        response.Error.Should().Contain("not found",
            "the failure keeps its own reason");
        response.Error.Should().Contain("per-delivery content budget",
            "#3101: the file that cannot fit any delivery is still named with its size and the limit");
        response.Error.Should().Contain("Out-of-band transfer was unavailable",
            "and #3233 adds WHY the bytes had to travel inline — the operator can act on that");
    }
}

/// <summary>
/// The receiving half of the honesty contract: a staged handle that does not resolve is a NAMED
/// FAILURE, never a silent write of nothing. Driven straight at the real
/// <c>SyncContentFilesRequest</c> handler with a handle nothing ever staged — the shape a producer
/// crash between staging and posting would produce.
/// </summary>
public class UnresolvableStagedHandleIsALoudFailureTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly string contentPath = Path.Combine(
        AppContext.BaseDirectory, "Files", "OutOfBandMissing", Guid.NewGuid().ToString("N")[..8]);

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration configuration)
        => base.ConfigureMesh(configuration).WithTypes(
            typeof(SyncContentFilesRequest), typeof(InlineContentFile),
            typeof(StagedContentFile), typeof(ImportContentResponse));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        Directory.CreateDirectory(contentPath);
        return base.ConfigureHost(configuration)
            .WithTypes(typeof(SyncContentFilesRequest), typeof(InlineContentFile),
                typeof(StagedContentFile), typeof(ImportContentResponse))
            .AddContentCollections()
            .AddFileSystemContentCollection("content", _ => contentPath);
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).WithTypes(
            typeof(SyncContentFilesRequest), typeof(InlineContentFile),
            typeof(StagedContentFile), typeof(ImportContentResponse));

    [Fact]
    public async Task A_handle_nothing_staged_fails_by_name_and_writes_nothing()
    {
        var request = new SyncContentFilesRequest("content", string.Empty, [])
        {
            Mirror = false,
            StagedFiles = [new StagedContentFile("videos/ghost.mp4", new string('a', 64), 1234)]
        };

        var response = await GetClient()
            .Observe(request, o => o.WithTarget(CreateHostAddress()))
            .Select(d => d.Message)
            .Take(1)
            .Timeout(TestTimeouts.Convergence)
            .FirstAsync();

        response.Success.Should().BeFalse(
            "a handle that does not resolve must never be folded into 'zero files' — that is the "
            + "#3101 defect one layer down");
        response.Error.Should().Contain("staging area");
        response.Error.Should().Contain("was NOT written");
        File.Exists(Path.Combine(contentPath, "videos", "ghost.mp4")).Should().BeFalse(
            "an unresolvable handle must not leave an empty file behind");
    }
}
