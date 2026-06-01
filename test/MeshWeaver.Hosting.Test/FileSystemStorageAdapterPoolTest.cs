using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// End-to-end wiring test: proves that <see cref="FileSystemStorageAdapter"/>
/// actually routes its disk I/O through the injected <see cref="IIoPool"/> and
/// that the pool's concurrency cap is honoured. A cap-1 pool must serialize a
/// burst of concurrent reads.
/// </summary>
public sealed class FileSystemStorageAdapterPoolTest : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly string _dir;

    public FileSystemStorageAdapterPoolTest()
    {
        _dir = Path.Combine(Path.GetTempPath(), "MeshWeaver-IoPoolWiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Reads_route_through_the_injected_pool_and_respect_its_cap()
    {
        const int cap = 1;
        const int count = 12;
        using var realPool = new IoPool(cap);
        var rec = new RecordingPool(realPool);
        var adapter = new FileSystemStorageAdapter(_dir, writeOptionsModifier: null, pool: rec);

        // Seed nodes on disk (sequential).
        foreach (var i in Enumerable.Range(0, count))
        {
            await adapter.Write(new MeshNode($"item{i}", "Items")
            {
                Name = $"item{i}", NodeType = "Markdown", State = MeshNodeState.Active,
            }, JsonOptions).ToTask();
        }

        var invokesBeforeReads = rec.Invokes;

        // Fire all reads at once; the cap-1 pool must serialize them.
        var paths = Enumerable.Range(0, count).Select(i => $"Items/item{i}").ToArray();
        var results = await Task.WhenAll(paths.Select(p => adapter.Read(p, JsonOptions).ToTask()));

        results.Should().OnlyContain(n => n != null, "every seeded node should read back");
        (rec.Invokes - invokesBeforeReads).Should().Be(count, "each Read must go through the injected pool");
        rec.Max.Should().Be(cap, "the cap-1 pool must serialize concurrent reads");
    }

    /// <summary>Decorates a real <see cref="IoPool"/>, recording call count and peak concurrency.</summary>
    private sealed class RecordingPool : IIoPool
    {
        private readonly IIoPool _inner;
        private int _current;
        private int _invokes;

        public RecordingPool(IIoPool inner) => _inner = inner;

        public int Invokes => Volatile.Read(ref _invokes);
        public int Max { get; private set; }
        public int CurrentInFlight => _inner.CurrentInFlight;

        public IObservable<T> Invoke<T>(Func<CancellationToken, Task<T>> io)
        {
            Interlocked.Increment(ref _invokes);
            return _inner.Invoke(async ct =>
            {
                var c = Interlocked.Increment(ref _current);
                lock (this) { if (c > Max) Max = c; }
                try { return await io(ct); }
                finally { Interlocked.Decrement(ref _current); }
            });
        }

        public IObservable<T> InvokeStream<T>(Func<CancellationToken, IAsyncEnumerable<T>> source)
            => _inner.InvokeStream(source);

        public IObservable<T> InvokeBlocking<T>(Func<CancellationToken, T> work)
            => _inner.InvokeBlocking(work);
    }
}
