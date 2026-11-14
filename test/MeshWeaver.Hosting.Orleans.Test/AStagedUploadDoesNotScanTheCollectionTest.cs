using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>MeshWeaver#8158 — staging one upload must not scan the destination collection.</b>
/// An upload over one delivery's 1 MiB budget travels out of band: the POSTING hub (on the
/// co-hosted portal, the silo's mesh hub — where the Executive Assistant's <c>SaveAttachment</c>
/// runs) resolves the destination collection and writes the bytes into its <c>_staging</c> folder.
/// It resolved it through <see cref="IContentService.GetCollection"/>, which INITIALIZES the
/// collection: it reads and parses every markdown file in the whole tree and attaches a change
/// watcher — on a hub that only wants to write one blob, with no bound on how long that takes.
/// On a client partition's <c>content</c> share that is a network round-trip per file, and the
/// upload waited for all of it before its write was even posted.
///
/// <para>The collection's provider here parks its whole-tree scan (the one read only
/// <see cref="ContentCollection.Initialize"/> issues) for as long as the test holds it — a share
/// whose scan takes longer than any request deadline. The owner's own collection is initialized
/// BEFORE the park is armed (by the small upload), exactly as a live owner that has served the
/// collection for days. So, with the park armed, an upload can only stall if something ELSE
/// starts a fresh scan — and the only other hub on the path is the producer.</para>
///
/// <para>Controls: the inline upload under the armed park (the producer stages nothing, so it
/// must land — the park blocks a scan and nothing else), and the park's own scan counter, which
/// must read zero when the staged upload has landed — a scan that merely finished fast would
/// still count.</para>
/// </summary>
public class AStagedUploadDoesNotScanTheCollectionTest(ITestOutputHelper output) : OrleansMeshTestBase(output)
{
    /// <inheritdoc />
    protected override Type SiloConfiguratorType => typeof(Configurator);

    private const string ParkableSourceType = "ParkableFileSystem";

    internal static readonly string Partition = "UploadSpace" + Guid.NewGuid().ToString("N")[..8];

    internal static readonly string ContentRoot =
        Path.Combine(Path.GetTempPath(), "AStagedUploadDoesNotScanTheCollectionTest", Guid.NewGuid().ToString("N"));

    /// <summary>Far longer than the upload's own bound below, so a parked scan can only show as a stall.</summary>
    private static readonly TimeSpan ParkBound = TimeSpan.FromSeconds(90);

    /// <summary>What a landed upload takes here (~0.2 s measured) with two orders of magnitude to spare.</summary>
    private static readonly TimeSpan UploadBound = TimeSpan.FromSeconds(20);

    private IMessageHub Mesh =>
        ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services.GetRequiredService<IMessageHub>();

    private ScanPark Park =>
        ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services.GetRequiredService<ScanPark>();

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        new Random(4711).NextBytes(bytes);
        return bytes;
    }

    private async Task<(string Answer, TimeSpan Took)> Upload(string path, byte[] bytes)
    {
        var watch = Stopwatch.StartNew();
        var answer = await new MeshOperations(Mesh).Upload(path, bytes)
            .Take(1)
            .Timeout(UploadBound)
            .Catch<string, Exception>(ex => Observable.Return($"FAULT {ex.GetType().Name}: {ex.Message}"))
            .Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"{bytes.Length:N0} bytes → {path} in {watch.Elapsed.TotalSeconds:0.00}s: {answer}");
        return (answer, watch.Elapsed);
    }

    [Fact(Timeout = 180_000)]
    public async Task AStagedUpload_DoesNotWaitForTheProducerToScanTheCollection()
    {
        // The owner serves its collection — initialized, scan done — before anything is parked.
        var warm = await Upload($"{Partition}/content/attachments/warm.pdf", Bytes(251_034));
        warm.Answer.Should().Contain("\"Uploaded\"", "an inline upload under no park must land, or nothing below means anything");

        var large = Bytes(4_900_000);
        ContentDeliveryBudget.PackagedCost("attachments/large.docx", large.Length)
            .Should().BeGreaterThan(ContentDeliveryBudget.BudgetBytes, "the file must take the out-of-band path the production save took");

        Park.Arm();
        try
        {
            var inline = await Upload($"{Partition}/content/attachments/inline-under-park.pdf", Bytes(251_034));
            inline.Answer.Should().Contain("\"Uploaded\"",
                "CONTROL: the park blocks a whole-collection scan and nothing else — the owner is warm, so a write that stages nothing lands");

            var staged = await Upload($"{Partition}/content/attachments/large.docx", large);
            staged.Answer.Should().Contain("\"Uploaded\"",
                "staging one blob needs the destination's STORE, not its parsed markdown — the upload must not wait on a scan of the whole collection");
            Park.ScansWhileArmed.Should().Be(0,
                "nothing on the upload path may start a fresh scan of the destination collection; the producer opened it to write one blob");
            (await File.ReadAllBytesAsync(Path.Combine(ContentRoot, Partition, "attachments", "large.docx"),
                    TestContext.Current.CancellationToken))
                .Should().Equal(large);
        }
        finally
        {
            Park.Release();
        }
    }

    /// <summary>
    /// The test's hold on the collection provider's whole-tree scan. A mesh-scoped singleton (never
    /// static state); released in the test's <c>finally</c> so a failing assertion cannot strand the
    /// parked pool thread.
    /// </summary>
    public sealed class ScanPark
    {
        private volatile int armed;
        private volatile int released;
        private int scans;

        /// <summary>Scans started while armed.</summary>
        public int ScansWhileArmed => Volatile.Read(ref scans);

        /// <summary>From now on, a whole-tree scan parks until <see cref="Release"/>.</summary>
        public void Arm() => armed = 1;

        /// <summary>Lets every parked scan go on.</summary>
        public void Release() => released = 1;

        /// <summary>Called at the start of a whole-tree scan.</summary>
        public void OnScan()
        {
            if (armed == 0)
                return;
            Interlocked.Increment(ref scans);
            SpinWait.SpinUntil(() => released == 1, ParkBound);
        }
    }

    /// <summary>A file-system provider whose whole-tree scan can be parked.</summary>
    private sealed class ParkableProvider(string basePath, ScanPark park) : IStreamProvider
    {
        private readonly FileSystemStreamProvider inner = new(basePath);

        public string ProviderType => ParkableSourceType;

        public Task<Stream?> GetStreamAsync(string reference, CancellationToken cancellationToken = default)
            => inner.GetStreamAsync(reference, cancellationToken);

        public Task<(Stream? Stream, string Path, DateTime LastModified)> GetStreamWithMetadataAsync(
            string path, CancellationToken cancellationToken = default)
            => inner.GetStreamWithMetadataAsync(path, cancellationToken);

        public Task WriteStreamAsync(string reference, Stream content, CancellationToken cancellationToken = default)
            => inner.WriteStreamAsync(reference, content, cancellationToken);

        public async IAsyncEnumerable<(Stream? Stream, string Path, DateTime LastModified)> GetStreamsAsync(
            Func<string, bool> filter, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            park.OnScan();
            await foreach (var item in inner.GetStreamsAsync(filter, cancellationToken).ConfigureAwait(false))
                yield return item;
        }

        public IAsyncEnumerable<FolderItem> GetFolders(string path, CancellationToken ct = default)
            => inner.GetFolders(path, ct);

        public IAsyncEnumerable<FileItem> GetFiles(string path, CancellationToken ct = default)
            => inner.GetFiles(path, ct);

        public Task SaveFileAsync(string path, string fileName, Stream content, CancellationToken cancellationToken = default)
            => inner.SaveFileAsync(path, fileName, content, cancellationToken);

        public Task CreateFolderAsync(string folderPath) => inner.CreateFolderAsync(folderPath);

        public Task DeleteFolderAsync(string folderPath) => inner.DeleteFolderAsync(folderPath);

        public Task DeleteFileAsync(string filePath) => inner.DeleteFileAsync(filePath);

        public IDisposable? AttachMonitor(Action<string> onChanged) => inner.AttachMonitor(onChanged);

        public Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken cancellationToken = default)
            => inner.LoadAuthorsAsync(cancellationToken);
    }

    private sealed class ParkableFactory(ScanPark park) : IStreamProviderFactory
    {
        public IObservable<IStreamProvider> Create(ContentCollectionConfig config)
            => Observable.Return<IStreamProvider>(new ParkableProvider(
                config.BasePath ?? config.Settings?.GetValueOrDefault("BasePath") ?? "", park));
    }

    /// <summary>
    /// A Space root with a per-node <c>content</c> collection, wired as the portal wires it
    /// (<c>ConfigureDefaultNodeHub</c> on partition roots), and content collections on the mesh hub
    /// that posts the upload — the co-hosted portal registers its <c>storage</c> source there.
    /// </summary>
    public class Configurator : TestSiloConfigurator
    {
        protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
            builder
                .AddSpaceType()
                .AddMeshNodes(new MeshNode(Partition) { Name = Partition, NodeType = "Space", State = MeshNodeState.Active })
                .ConfigureServices(services => services
                    .AddSingleton<ScanPark>()
                    .AddKeyedSingleton<IStreamProviderFactory>(ParkableSourceType,
                        (sp, _) => new ParkableFactory(sp.GetRequiredService<ScanPark>())))
                .ConfigureHub(config => config.AddContentCollections())
                .ConfigureDefaultNodeHub(config => config.Address.ToString().Contains('/')
                    ? config
                    : config.AddContentCollection(_ => new ContentCollectionConfig
                    {
                        Name = "content",
                        SourceType = ParkableSourceType,
                        BasePath = Path.Combine(ContentRoot, config.Address.ToString()),
                        IsEditable = true,
                        ExposeInChildren = true,
                    }));
    }
}
