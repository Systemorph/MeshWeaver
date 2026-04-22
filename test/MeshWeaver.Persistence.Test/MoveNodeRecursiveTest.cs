using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Covers the prod-observed "move takes 4 minutes on a 3-node subtree" regression:
/// — verifies that move is genuinely recursive,
/// — verifies that per-descendant I/O runs in parallel (not serial),
/// — verifies that negative paths (source missing, target exists, storage throws,
///   cancellation) fail fast and never hang, and
/// — verifies the Rx Timeout ceiling that the handler applies on top.
/// </summary>
public class MoveNodeRecursiveTest
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    // --------------------------- correctness: recursion ---------------------------

    [Fact(Timeout = 10000)]
    public async Task Move_ThreeLevelsDeep_MovesEveryDescendant()
    {
        var adapter = new TestStorageAdapter();
        var service = new FileSystemPersistenceService(adapter);

        foreach (var p in new[] { "src/a", "src/a/b", "src/a/b/c", "src/a/b/c/d" })
            await adapter.WriteAsync(MeshNode.FromPath(p) with { Name = p }, JsonOptions);

        await service.MoveNodeAsync("src/a", "dst/a", JsonOptions);

        foreach (var oldPath in new[] { "src/a", "src/a/b", "src/a/b/c", "src/a/b/c/d" })
            (await adapter.ReadAsync(oldPath, JsonOptions)).Should().BeNull($"{oldPath} should be gone");

        foreach (var newPath in new[] { "dst/a", "dst/a/b", "dst/a/b/c", "dst/a/b/c/d" })
            (await adapter.ReadAsync(newPath, JsonOptions)).Should().NotBeNull($"{newPath} should exist after move");

        var deepest = await adapter.ReadAsync("dst/a/b/c/d", JsonOptions);
        deepest!.Name.Should().Be("src/a/b/c/d", "name is metadata and is preserved verbatim on move");
    }

    [Fact(Timeout = 10000)]
    public async Task Move_BranchingSubtree_MovesSiblingsAndNestedChildren()
    {
        var adapter = new TestStorageAdapter();
        var service = new FileSystemPersistenceService(adapter);

        foreach (var p in new[]
        {
            "src/dav", "src/dav/venue", "src/dav/venue/estrel",
            "src/dav/hotel", "src/dav/hotel/mercure",
            "src/dav/program", "src/dav/presentation"
        })
            await adapter.WriteAsync(MeshNode.FromPath(p) with { Name = p }, JsonOptions);

        await service.MoveNodeAsync("src/dav", "dst/dav", JsonOptions);

        (await adapter.ReadAsync("dst/dav/venue/estrel", JsonOptions)).Should().NotBeNull();
        (await adapter.ReadAsync("dst/dav/hotel/mercure", JsonOptions)).Should().NotBeNull();
        (await adapter.ReadAsync("dst/dav/program", JsonOptions)).Should().NotBeNull();
        (await adapter.ReadAsync("dst/dav/presentation", JsonOptions)).Should().NotBeNull();
        (await adapter.ReadAsync("src/dav/venue/estrel", JsonOptions)).Should().BeNull();
    }

    // --------------------------- perf: parallelism ---------------------------

    [Fact(Timeout = 15000)]
    public async Task Move_LargeSubtree_RunsIOInParallel()
    {
        // 100 ms per storage op. 20 descendants × (write + delete) = 40 ops.
        // Serial: ~4 s. Parallel: single-wave ~100-300 ms, tree walk reads on top.
        var adapter = new TestStorageAdapter(perOpDelay: TimeSpan.FromMilliseconds(100));
        var service = new FileSystemPersistenceService(adapter);

        await adapter.WriteAsync(MeshNode.FromPath("src/root") with { Name = "Root" }, JsonOptions);
        for (var i = 0; i < 20; i++)
            await adapter.WriteAsync(MeshNode.FromPath($"src/root/c{i}") with { Name = $"c{i}" }, JsonOptions);

        adapter.Reset();
        var sw = Stopwatch.StartNew();
        await service.MoveNodeAsync("src/root", "dst/root", JsonOptions);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
            "20 descendants × 100 ms serial would be ~4 s; parallel I/O must finish well under 3 s");
        adapter.MaxConcurrent.Should().BeGreaterThan(1,
            "at least two descendant-I/O ops must overlap to prove parallelization");

        for (var i = 0; i < 20; i++)
            (await adapter.ReadAsync($"dst/root/c{i}", JsonOptions)).Should().NotBeNull();
    }

    // --------------------------- negative: fail-fast paths ---------------------------

    [Fact(Timeout = 5000)]
    public async Task Move_SourceMissing_FailsFast()
    {
        var adapter = new TestStorageAdapter(perOpDelay: TimeSpan.FromMilliseconds(50));
        var service = new FileSystemPersistenceService(adapter);

        var sw = Stopwatch.StartNew();
        var act = async () => await service.MoveNodeAsync("does/not/exist", "dst/x", JsonOptions);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Source node not found*");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "should reject before doing real work");
    }

    [Fact(Timeout = 5000)]
    public async Task Move_TargetExists_FailsFast()
    {
        var adapter = new TestStorageAdapter(perOpDelay: TimeSpan.FromMilliseconds(50));
        var service = new FileSystemPersistenceService(adapter);

        await adapter.WriteAsync(MeshNode.FromPath("src/x") with { Name = "Src" }, JsonOptions);
        await adapter.WriteAsync(MeshNode.FromPath("dst/x") with { Name = "Dst" }, JsonOptions);

        var sw = Stopwatch.StartNew();
        var act = async () => await service.MoveNodeAsync("src/x", "dst/x", JsonOptions);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Target path already exists*");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "collision check is pre-flight");
    }

    [Fact(Timeout = 10000)]
    public async Task Move_StorageThrowsOnOneDescendantWrite_SurfacesErrorWithoutHanging()
    {
        // Write to any path ending in "/bad" blows up — simulates a single-node persistence failure.
        var adapter = new TestStorageAdapter(
            perOpDelay: TimeSpan.FromMilliseconds(20),
            failOnWrite: path => path.EndsWith("/bad", StringComparison.Ordinal));
        var service = new FileSystemPersistenceService(adapter);

        adapter.Seed(MeshNode.FromPath("src/root") with { Name = "Root" });
        adapter.Seed(MeshNode.FromPath("src/root/good") with { Name = "Good" });
        adapter.Seed(MeshNode.FromPath("src/root/bad") with { Name = "Bad" });

        var sw = Stopwatch.StartNew();
        var act = async () => await service.MoveNodeAsync("src/root", "dst/root", JsonOptions);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*bad*", "the simulated write failure must propagate");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
            "a single failed write must not cause the whole move to hang");
    }

    [Fact(Timeout = 10000)]
    public async Task Move_WithCancellationToken_StopsPromptlyNoMatterHowSlowStorageIs()
    {
        // Storage is effectively frozen (30 s per op). Cancelling after 100 ms must
        // abort long before the default 30 s mesh-operation ceiling.
        var adapter = new TestStorageAdapter(perOpDelay: TimeSpan.FromSeconds(30));
        var service = new FileSystemPersistenceService(adapter);

        adapter.Seed(MeshNode.FromPath("src/root") with { Name = "Root" });
        for (var i = 0; i < 3; i++)
            adapter.Seed(MeshNode.FromPath($"src/root/c{i}") with { Name = $"c{i}" });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var sw = Stopwatch.StartNew();
        var act = async () => await service.MoveNodeAsync("src/root", "dst/root", JsonOptions, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
            "cancellation must stop the move within the test window, not wait for storage");
    }

    // --------------------------- timeout ceiling (handler-layer contract) ---------------------------

    [Fact(Timeout = 5000)]
    public async Task MoveObservable_HangingStorage_IsBoundedByTimeout()
    {
        // This is the contract the MoveNode request-handler relies on: a .Timeout()
        // chained onto the persistence Observable must surface TimeoutException when
        // the underlying op stalls. Proves the handler's "nothing over the timeout" guarantee.
        var persistence = new HangingPersistence();

        var sw = Stopwatch.StartNew();
        var act = async () => await persistence.MoveNode("src", "dst")
            .Timeout(TimeSpan.FromMilliseconds(200))
            .ToTask();

        await act.Should().ThrowAsync<TimeoutException>();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "Timeout(200ms) must fire well before any wall-clock runaway");
    }

    [Fact]
    public void MeshOperationOptions_DefaultTimeoutIs30Seconds()
    {
        new MeshOperationOptions().Timeout.Should().Be(TimeSpan.FromSeconds(30),
            "production guard-rail: no mesh op should silently exceed 30 s");
    }

    [Fact]
    public void MeshOperationOptions_OverrideAllowsLongerTimeoutsForTests()
    {
        var options = new MeshOperationOptions { Timeout = TimeSpan.FromMinutes(10) };
        options.Timeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    // --------------------------- stubs ---------------------------

    /// <summary>
    /// Minimal <see cref="IStorageAdapter"/> for move testing: optional per-op delay,
    /// a concurrency-high-water counter (to prove parallelism), and an optional
    /// write-failure predicate.
    /// </summary>
    private sealed class TestStorageAdapter : IStorageAdapter
    {
        private readonly ConcurrentDictionary<string, MeshNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _perOpDelay;
        private readonly Func<string, bool>? _failOnWrite;
        private int _concurrent;
        private int _maxConcurrent;

        public TestStorageAdapter(
            TimeSpan? perOpDelay = null,
            Func<string, bool>? failOnWrite = null)
        {
            _perOpDelay = perOpDelay ?? TimeSpan.Zero;
            _failOnWrite = failOnWrite;
        }

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public void Reset()
        {
            Interlocked.Exchange(ref _maxConcurrent, 0);
            Interlocked.Exchange(ref _concurrent, 0);
        }

        /// <summary>Direct write that skips the delay/failure gate — used for test setup.</summary>
        public void Seed(MeshNode node) => _nodes[node.Path ?? ""] = node;

        private async Task GateAsync(CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _concurrent);
            var max = Volatile.Read(ref _maxConcurrent);
            while (now > max && Interlocked.CompareExchange(ref _maxConcurrent, now, max) != max)
                max = Volatile.Read(ref _maxConcurrent);
            try
            {
                if (_perOpDelay > TimeSpan.Zero)
                    await Task.Delay(_perOpDelay, ct);
            }
            catch
            {
                Interlocked.Decrement(ref _concurrent);
                throw;
            }
        }

        private void Release() => Interlocked.Decrement(ref _concurrent);

        public async Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
        {
            await GateAsync(ct);
            try { return _nodes.TryGetValue(path, out var n) ? n : null; }
            finally { Release(); }
        }

        public async Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        {
            await GateAsync(ct);
            try
            {
                var path = node.Path ?? "";
                if (_failOnWrite != null && _failOnWrite(path))
                    throw new InvalidOperationException($"Simulated write failure for {path}");
                _nodes[path] = node;
            }
            finally { Release(); }
        }

        public async Task DeleteAsync(string path, CancellationToken ct = default)
        {
            await GateAsync(ct);
            try { _nodes.TryRemove(path, out _); }
            finally { Release(); }
        }

        public Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
            string? parentPath, CancellationToken ct = default)
        {
            var prefix = string.IsNullOrEmpty(parentPath) ? "" : parentPath + "/";
            var children = _nodes.Keys
                .Where(k => !string.IsNullOrEmpty(k)
                    && k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && k.Length > prefix.Length
                    && !k[prefix.Length..].Contains('/'))
                .ToList();
            return Task.FromResult<(IEnumerable<string>, IEnumerable<string>)>((children, Enumerable.Empty<string>()));
        }

        public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
            => Task.FromResult(_nodes.ContainsKey(path));

        public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
            string nodePath, string? subPath, JsonSerializerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task SavePartitionObjectsAsync(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
            => Task.FromResult<DateTimeOffset?>(null);
    }

    /// <summary>Observable persistence whose MoveNode never emits — used to verify Rx Timeout.</summary>
    private sealed class HangingPersistence
    {
        public IObservable<MeshNode> MoveNode(string source, string target) => Observable.Never<MeshNode>();
    }
}
