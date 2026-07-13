#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the fresh-mesh install path: on a mesh where NOTHING has ever written to the
/// <c>Plugins</c> records partition, <see cref="PackageInstaller"/> must eagerly provision the
/// involved partitions via <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/>
/// (the same mechanism the static-repo importer and the Space-create path use) BEFORE its first
/// write. Without that, the very first catalog install on a clean Postgres-backed mesh faults
/// with <c>42P01</c> (relation does not exist), because <c>Plugins</c> is not an OwnsPartition
/// type and the storage router no longer lazily creates schemas.
///
/// <para>The in-memory test backend needs no provisioning (its
/// <c>EnsurePartitionProvisioned</c> no-ops), so the pin is a recording provider registered
/// alongside it: if the installer stops ensuring the partitions, the recorded set goes empty and
/// this test fails — independent of the storage backend.</para>
/// </summary>
public class FreshMeshPartitionProvisioningTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly RecordingPartitionProvider recorder = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(s =>
            {
                s.AddSingleton<IPartitionStorageProvider>(recorder);
                return s;
            });

    [Fact(Timeout = 120000)]
    public async Task Install_OnFreshMesh_ProvisionsRecordsAndTargetPartitions_BeforeWriting()
    {
        var manifest = new PackageManifest
        {
            Id = "fresh-pack",
            Name = "Fresh Pack",
            Kind = PackageKind.Content,
            TargetPartition = "FreshTarget",
            SourceFolder = "fresh-pack",
            Version = "1.0.0",
        };
        var files = new[] { new PackageFile("fresh-pack/Doc.md", "# Doc\n\nFresh-mesh install.") };

        var result = await PackageInstaller.Install(Mesh, manifest, files, "HEAD").FirstAsync().ToTask();
        result.Written.Should().Be(1);

        // The installer must have run the standard ensure-partition mechanism for BOTH the
        // install-records partition and the content's target partition. On Postgres this is what
        // creates the schema + tables; skipping it is the fresh-mesh 42P01.
        recorder.Provisioned.Should().Contain(PackageInstaller.InstalledPartition);
        recorder.Provisioned.Should().Contain("FreshTarget");

        // And the ensure ran BEFORE the writes: the recorder snapshots the provisioned set the
        // first time each partition is touched by a write.
        recorder.ProvisionedBeforeFirstTouch(PackageInstaller.InstalledPartition).Should().BeTrue(
            "the Plugins records partition must be provisioned before the install record is written");
        recorder.ProvisionedBeforeFirstTouch("FreshTarget").Should().BeTrue(
            "the target partition must be provisioned before content nodes are written");

        // The install record itself landed (in-memory backend accepts the write regardless).
        var record = await Mesh.GetWorkspace()
            .GetMeshNodeStream($"{PackageInstaller.InstalledPartition}/fresh-pack")
            .Where(n => n is not null).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        record!.NodeType.Should().Be(PackageInstaller.PackageNodeType);
    }

    /// <summary>
    /// Records <see cref="EnsurePartitionProvisioned"/> calls, and — through its (otherwise inert,
    /// read-only) adapter — whether a partition's first read/write touch happened before or after
    /// its provisioning. Read-only + priority 0, so it never participates in the write chain and
    /// its null answers never shadow the in-memory backend.
    /// </summary>
    private sealed class RecordingPartitionProvider : IPartitionStorageProvider
    {
        private ImmutableList<string> provisioned = [];
        private ImmutableDictionary<string, bool> firstTouchProvisioned = ImmutableDictionary<string, bool>.Empty;
        private readonly object gate = new();

        public string Name => "Recording";
        public bool IsReadOnly => true;
        public int Priority => 0;
        public IStorageAdapter Adapter => new TouchRecordingAdapter(this);

        public IReadOnlyList<string> Provisioned
        {
            get { lock (gate) return provisioned; }
        }

        public bool ProvisionedBeforeFirstTouch(string partition)
        {
            lock (gate)
                // Never touched at all counts as success only if it WAS provisioned — a partition
                // that was neither provisioned nor touched is a test-setup error surfaced by the
                // Contain assertions above.
                return firstTouchProvisioned.GetValueOrDefault(partition, provisioned.Contains(partition));
        }

        public IObservable<Unit> EnsurePartitionProvisioned(string @namespace)
        {
            lock (gate)
                provisioned = provisioned.Add(@namespace);
            return Observable.Return(Unit.Default);
        }

        private void RecordTouch(string path)
        {
            var partition = path.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } segs
                ? segs[0]
                : path;
            lock (gate)
            {
                if (!firstTouchProvisioned.ContainsKey(partition))
                    firstTouchProvisioned = firstTouchProvisioned.Add(partition, provisioned.Contains(partition));
            }
        }

        private sealed class TouchRecordingAdapter(RecordingPartitionProvider owner) : IStorageAdapter
        {
            public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            {
                owner.RecordTouch(path);
                return Observable.Return<MeshNode?>(null);
            }

            // null = "not my path" — the routing chain moves on to the real (in-memory) backend.
            public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            {
                owner.RecordTouch(node.Path);
                return Observable.Return<MeshNode?>(null);
            }

            public IObservable<string> Delete(string path) => Observable.Empty<string>();

            public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
                => Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []));

            public IObservable<bool> Exists(string path) => Observable.Return(false);

            public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
                => Observable.Empty<object>();

            public IObservable<Unit> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
                => Observable.Return(Unit.Default);

            public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
                => Observable.Return(Unit.Default);

            public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
                => Observable.Return<DateTimeOffset?>(null);
        }
    }
}
