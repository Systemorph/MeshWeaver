using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins <see cref="IStorageAdapter.WriteMany"/>'s DEFAULT implementation — the one every adapter
/// that doesn't override it inherits (FileSystem, InMemory, AzureBlob, Sqlite, Cosmos).
///
/// <para>The interesting property is ORDER, not throughput. Callers order parents before children
/// on purpose — the installer's <c>CopyOrder</c> sorts by path depth — because activating a child's
/// per-node hub while its parent's is still cold is the cold-activation race that used to wedge
/// installs. <c>ReadMany</c>'s default is a <c>Merge</c> and that is fine for reads; a write default
/// that merged would silently reintroduce that race, which is exactly the kind of regression a test
/// has to hold down.</para>
/// </summary>
public class StorageAdapterWriteManyTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    // WriteMany is a DEFAULT interface member, so it is reachable only through IStorageAdapter —
    // an adapter that does not override it has no such method on its own type. That is the point:
    // these tests exercise the inherited default, not an override.
    // MeshNode(id, @namespace) — Path is DERIVED as "{namespace}/{id}", not passed in.
    private static MeshNode Node(string id, string ns) =>
        new(id, ns) { Name = id, NodeType = "Markdown", State = MeshNodeState.Active };

    [Fact]
    public async Task Default_writes_strictly_in_caller_order()
    {
        var adapter = new OrderRecordingAdapter();
        var nodes = new[]
        {
            Node("Course", "me"),
            Node("Lesson", "me/Course"),
            Node("Exercise", "me/Course/Lesson"),
        };

        var written = await ((IStorageAdapter)adapter).WriteMany(nodes, JsonOptions).FirstAsync();

        Assert.Equal(
            new[] { "me/Course", "me/Course/Lesson", "me/Course/Lesson/Exercise" },
            adapter.WriteOrder);
        Assert.Equal(3, written.Count);
    }

    [Fact]
    public async Task Default_does_not_overlap_writes()
    {
        // Concat, not Merge: a write must complete before the next one is subscribed, or a child
        // can reach storage while its parent's hub is still activating.
        var adapter = new OrderRecordingAdapter();

        await ((IStorageAdapter)adapter).WriteMany(
            Enumerable.Range(0, 5).Select(i => Node($"N{i}", "me")).ToArray(),
            JsonOptions).FirstAsync();

        Assert.Equal(1, adapter.MaxConcurrent);   // never more than one write in flight at a time
    }

    [Fact]
    public async Task Default_omits_nodes_the_adapter_does_not_own()
    {
        // Write emits null for an unowned path so PersistenceService's try-then-claim chain moves
        // on; WriteMany must drop those rather than surfacing nulls.
        var adapter = new OrderRecordingAdapter { UnownedPrefix = "other/" };

        var written = await ((IStorageAdapter)adapter).WriteMany(
            new[] { Node("Mine", "me"), Node("Theirs", "other") },
            JsonOptions).FirstAsync();

        Assert.Equal(new[] { "me/Mine" }, written.Select(n => n.Path));
    }

    [Fact]
    public async Task Default_on_empty_input_emits_an_empty_list()
    {
        var written = await ((IStorageAdapter)new OrderRecordingAdapter())
            .WriteMany([], JsonOptions).FirstAsync();

        Assert.Empty(written);
    }

    /// <summary>Records write order and peak overlap; everything else is inert.</summary>
    private sealed class OrderRecordingAdapter : IStorageAdapter
    {
        private int _inFlight;
        public List<string> WriteOrder { get; } = [];
        public int MaxConcurrent { get; private set; }
        public string? UnownedPrefix { get; init; }

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => Observable.Return<MeshNode?>(null);

        // 🚨 The write must stay IN FLIGHT until its observable terminates, and must terminate
        // ASYNCHRONOUSLY. A synchronous Observable.Return that decremented before returning made
        // MaxConcurrent cap at 1 no matter what WriteMany did — so the overlap test passed against a
        // Merge implementation too, i.e. it pinned nothing. Holding the count in Finally() and
        // completing on the task pool gives a concurrent subscriber a real window to overlap in.
        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => Observable.Defer(() =>
            {
                if (UnownedPrefix != null && node.Path.StartsWith(UnownedPrefix, StringComparison.Ordinal))
                    return Observable.Return<MeshNode?>(null);
                var now = Interlocked.Increment(ref _inFlight);
                lock (WriteOrder)
                {
                    MaxConcurrent = Math.Max(MaxConcurrent, now);
                    WriteOrder.Add(node.Path);
                }
                return Observable.Return<MeshNode?>(node)
                    .Delay(TimeSpan.FromMilliseconds(5), TaskPoolScheduler.Default)
                    .Finally(() => Interlocked.Decrement(ref _inFlight));
            });

        public IObservable<string> Delete(string path) => Observable.Return(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(
            string? parentPath)
            => Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []));

        public IObservable<bool> Exists(string path) => Observable.Return(false);

        public IObservable<object> GetPartitionObjects(
            string nodePath, string? subPath, JsonSerializerOptions options)
            => Observable.Empty<object>();

        public IObservable<System.Reactive.Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => Observable.Return(System.Reactive.Unit.Default);

        public IObservable<System.Reactive.Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => Observable.Return(System.Reactive.Unit.Default);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => Observable.Return<DateTimeOffset?>(null);
    }
}
