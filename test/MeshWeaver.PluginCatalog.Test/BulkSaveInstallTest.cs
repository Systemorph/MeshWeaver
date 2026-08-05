#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the BULK-SAVE mechanics of a node-repo install (#815): a fresh multi-node install must
/// write its NEW non-root, non-satellite nodes through <see cref="IStorageAdapter.WriteMany"/>
/// batches — one per ordering bucket, whose response IS the visibility barrier — never through
/// per-node writes, while a re-install of the unchanged snapshot writes NOTHING (the
/// idempotence contract that guards every plugin-gate run). The assertions are
/// timing-insensitive: they check WHICH path the nodes travelled and the final stored state,
/// not how long anything took.
/// </summary>
public class BulkSaveInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly RecordingStorageAdapter _recorder = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog()
            .ConfigureServices(s =>
            {
                // Wrap the in-memory adapter so the test can observe which write PATH each node
                // took (WriteMany batch vs per-node Write). The wrapper forwards everything.
                s.RemoveAll<IStorageAdapter>();
                return s.AddSingleton<IStorageAdapter>(_recorder);
            });

    // A course-shaped node-repo plugin: a static-typed Space root, one NodeType with its Source,
    // and several plain content instances — the population whose one-at-a-time writes made a
    // ~300-node course install pay minutes of serial round-trips.
    private static readonly IReadOnlyList<RepoFile> Repo = new List<RepoFile>
    {
        new("BulkPack/index.json",
            """{"$type":"MeshNode","id":"BulkPack","namespace":"","path":"BulkPack","mainNode":"BulkPack","name":"Bulk Pack","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"bulk-save pin."}}"""),
        new("BulkPack/Thing.json",
            """{"$type":"MeshNode","id":"Thing","namespace":"BulkPack","path":"BulkPack/Thing","mainNode":"BulkPack/Thing","name":"Thing","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"a thing.","configuration":"config => config.WithContentType<Thing>()","includeGlobalTypes":true}}"""),
        new("BulkPack/Thing/Source/Thing.cs",
            "public record Thing { public string Name { get; init; } = string.Empty; }"),
        new("BulkPack/Lesson1.md", "# Lesson 1"),
        new("BulkPack/Lesson2.md", "# Lesson 2"),
        new("BulkPack/Lesson3.md", "# Lesson 3"),
        new("BulkPack/Deep.json",
            """{"$type":"MeshNode","id":"Deep","namespace":"BulkPack","path":"BulkPack/Deep","mainNode":"BulkPack/Deep","name":"Deep","nodeType":"BulkPack/Thing","state":"Active"}"""),
    };

    [Fact(Timeout = 120_000)]
    public async Task FreshInstall_BulkSavesNewNodes_PerBucket_AndReinstallWritesNothing()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-bulk", Repo));
        var source = new NodeRepoPackageSource(fetch, "https://github.com/acme/bulk");
        var manifest = new PackageManifest
        {
            Id = "BulkPack",
            Name = "Bulk Pack",
            Kind = PackageKind.NodeRepo,
            TargetPartition = "BulkPack",
            SourceFolder = "BulkPack",
            Version = "commit-bulk",
        };
        var files = await source.FetchPackageFiles(manifest, "HEAD").FirstAsync().ToTask();
        files.Count.Should().Be(7);

        var result = await PackageInstaller.Install(Mesh, manifest, files, "commit-bulk")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(90)).ToTask();

        // Correctness: everything landed, and WrittenPaths names all seven nodes.
        result.Total.Should().Be(7);
        result.Written.Should().Be(7);
        result.WrittenPaths.OrderBy(p => p, StringComparer.Ordinal).Should().Equal(
            "BulkPack", "BulkPack/Deep", "BulkPack/Lesson1", "BulkPack/Lesson2",
            "BulkPack/Lesson3", "BulkPack/Thing", "BulkPack/Thing/Source/Thing");
        (await Read("BulkPack")).NodeType.Should().Be("Space");
        (await Read("BulkPack/Thing")).NodeType.Should().Be("NodeType");
        (await Read("BulkPack/Lesson2")).NodeType.Should().Be("Markdown");
        (await Read("BulkPack/Deep")).NodeType.Should().Be("BulkPack/Thing",
            "a typed instance must land AFTER its in-package type's bulk batch committed");

        // Mechanism: the new non-root nodes travelled the BULK path — one WriteMany batch per
        // ordering bucket (compile inputs → types → instances), never a per-node write. The
        // batch RESPONSE is the visibility barrier, so bucket count is the whole story: no
        // 100 ms per-node Exists polling is left to observe.
        var batches = _recorder.WriteManyBatches;
        batches.Should().HaveCount(3,
            "a fresh install bulk-saves exactly its three ordering buckets: sources, types, instances");
        // bucket 0: the types' compile inputs; bucket 1: the type nodes — committed before any
        // instance batch is sent; bucket 2: plain content + typed instances, in ONE batch.
        batches[0].Should().Equal("BulkPack/Thing/Source/Thing");
        batches[1].Should().Equal("BulkPack/Thing");
        batches[2].OrderBy(p => p, StringComparer.Ordinal).Should().Equal(
            "BulkPack/Deep", "BulkPack/Lesson1", "BulkPack/Lesson2", "BulkPack/Lesson3");
        // The root is the one node that must keep the per-node request path (its create runs the
        // standard partition path), so it never appears in a bulk batch.
        batches.SelectMany(b => b).Should().NotContain("BulkPack");

        // Idempotence: the unchanged snapshot re-installs with ZERO writes — and with no new
        // bulk batches (the decisions run against the single ReadMany snapshot).
        var batchCountAfterInstall = _recorder.WriteManyBatches.Count;
        var second = await PackageInstaller.Install(Mesh, manifest, files, "commit-bulk")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(90)).ToTask();
        second.Written.Should().Be(0, "an unchanged node-repo re-install must not rewrite any node");
        _recorder.WriteManyBatches.Count.Should().Be(batchCountAfterInstall,
            "a re-install of the unchanged snapshot must not send any bulk batch");
    }

    [Fact(Timeout = 120_000)]
    public async Task WhenTheBulkReadFails_EveryNodeFallsBackToTheRequestPath()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-fallback", Repo));
        var source = new NodeRepoPackageSource(fetch, "https://github.com/acme/bulk");
        var manifest = new PackageManifest
        {
            Id = "BulkPack",
            Name = "Bulk Pack",
            Kind = PackageKind.NodeRepo,
            TargetPartition = "BulkPack",
            SourceFolder = "BulkPack",
            Version = "commit-fallback",
        };
        var files = await source.FetchPackageFiles(manifest, "HEAD").FirstAsync().ToTask();

        // Existence is UNKNOWN when the bulk read fails — bulk routing must be disabled
        // entirely (an empty-snapshot fallback would bulk-write nodes that may already exist,
        // bypassing the per-node handler path existing nodes require). Every node then takes
        // the validating request path, whose handler decides create-vs-update against its own
        // authoritative read — the pre-bulk installer's exact write-on-failure behavior.
        _recorder.FailReadMany = true;
        var result = await PackageInstaller.Install(Mesh, manifest, files, "commit-fallback")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(90)).ToTask();
        _recorder.FailReadMany = false;

        result.Written.Should().Be(7, "the degraded install still lands every node");
        _recorder.WriteManyBatches.Should().BeEmpty(
            "a failed bulk read must disable bulk routing, not route unknown-existence nodes to it");
        (await Read("BulkPack/Deep")).NodeType.Should().Be("BulkPack/Thing");
    }

    private async Task<MeshNode> Read(string path) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).Select(n => n!)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

    /// <summary>
    /// Pass-through <see cref="IStorageAdapter"/> that records every <see cref="WriteMany"/>
    /// batch's paths so the test can pin WHICH path a node's write travelled. Everything
    /// forwards to a real <see cref="InMemoryStorageAdapter"/> — including the change feed the
    /// synced-query providers subscribe to.
    /// </summary>
    private sealed class RecordingStorageAdapter : IStorageAdapter
    {
        private readonly InMemoryStorageAdapter _inner = new();
        private readonly ConcurrentQueue<IReadOnlyList<string>> _writeManyBatches = new();

        public IReadOnlyList<IReadOnlyList<string>> WriteManyBatches => _writeManyBatches.ToArray();

        /// <summary>When set, <see cref="ReadMany"/> errors — simulating a transient bulk-read failure.</summary>
        public bool FailReadMany { get; set; }

        public IObservable<DataChangeNotification> Changes => ((IStorageAdapter)_inner).Changes;

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => ((IStorageAdapter)_inner).Read(path, options);

        public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
            => FailReadMany
                ? Observable.Throw<MeshNode>(new InvalidOperationException("simulated bulk-read failure"))
                : ((IStorageAdapter)_inner).ReadMany(paths, options);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => ((IStorageAdapter)_inner).Write(node, options);

        public IObservable<IReadOnlyList<MeshNode>> WriteMany(
            IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
        {
            _writeManyBatches.Enqueue(nodes.Select(n => n.Path).ToImmutableList());
            return ((IStorageAdapter)_inner).WriteMany(nodes, options);
        }

        public IObservable<string> Delete(string path) => ((IStorageAdapter)_inner).Delete(path);

        public IObservable<bool> DeleteIfExists(string path) => ((IStorageAdapter)_inner).DeleteIfExists(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath)
            => ((IStorageAdapter)_inner).ListChildPaths(parentPath);

        public IObservable<bool> Exists(string path) => ((IStorageAdapter)_inner).Exists(path);

        public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
            string fullPath, JsonSerializerOptions options)
            => ((IStorageAdapter)_inner).FindBestPrefixMatch(fullPath, options);

        public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
            string fullPath, JsonSerializerOptions options)
            => ((IStorageAdapter)_inner).ResolvePath(fullPath, options);

        public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
            => ((IStorageAdapter)_inner).ListPartitionSubPaths(nodePath);

        public IObservable<object> GetPartitionObjects(
            string nodePath, string? subPath, JsonSerializerOptions options)
            => ((IStorageAdapter)_inner).GetPartitionObjects(nodePath, subPath, options);

        public IObservable<Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => ((IStorageAdapter)_inner).SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => ((IStorageAdapter)_inner).DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => ((IStorageAdapter)_inner).GetPartitionMaxTimestamp(nodePath, subPath);
    }
}
