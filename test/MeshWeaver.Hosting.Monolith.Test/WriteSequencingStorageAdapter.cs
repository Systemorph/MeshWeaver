#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Test-only INNERMOST <see cref="IStorageAdapter"/> decorator that (a) reports every write that
/// actually reached the store and (b) can HOLD a <em>non-advancing</em> write for a path — one whose
/// <see cref="MeshNode.Version"/> is at or below the highest version already written through here.
///
/// <para>A non-advancing write is precisely the shape of a DUPLICATE persistence route: the state has
/// already been written once at that version by another writer. Holding it and releasing it after the
/// row has advanced turns the production timing window (the sampler's <c>SaveMeshNodeRequest</c>
/// sitting in the owner's inbox while the post-commit flush advances the row) into a deterministic
/// sequence — no sleeps, no load dependence. On release the in-memory store's version-keeping upsert
/// refuses it and hands back the newer row, which is exactly the store-level conflict signal
/// <c>MonotonicWriteGuardStorageAdapter</c> resolves by merging (#971).</para>
///
/// <para>Register it BEFORE the test base's <c>AddInMemoryPersistence</c> (whose
/// <c>TryAddSingleton</c> then no-ops) so the write-integrity chain
/// (SubtreeDeletionGuard → MonotonicWriteGuard → VersionWriting) decorates THIS adapter — the same
/// placement <see cref="GatedReadStorageAdapter"/> documents.</para>
///
/// <para>Instance state only (mesh-scoped singleton, dies with the test mesh) — no statics.</para>
/// </summary>
internal sealed class WriteSequencingStorageAdapter(IStorageAdapter inner) : IStorageAdapter
{
    private readonly ReplaySubject<MeshNode> _writes = new();
    private readonly ReplaySubject<MeshNode> _heldWrites = new();
    private readonly ConcurrentDictionary<string, long> _armed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _floors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AsyncSubject<Unit>> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every write that REACHED the store, in order. Replayed, so a subscriber that attaches after
    /// the fact still sees the full history — an assertion about "how many writes happened" can
    /// therefore never depend on when it subscribed.
    /// </summary>
    public IObservable<MeshNode> Writes => _writes;

    /// <summary>Each non-advancing write that arrived at a closed gate (replayed).</summary>
    public IObservable<MeshNode> HeldWrites => _heldWrites;

    /// <summary>
    /// From now on, hold every write for <paramref name="path"/> that does not ADVANCE the highest
    /// version written through here — i.e. a duplicate of a state another writer already persisted.
    /// Advancing writes pass through and raise the mark.
    ///
    /// <para>Writes at or below <paramref name="afterVersion"/> are ignored entirely (passed through,
    /// mark untouched): the create pipeline legitimately writes the seed revision more than once
    /// (claim + type-source add), and that echo is not the duplicate under test.</para>
    /// </summary>
    public void HoldDuplicateWrites(string path, long afterVersion)
    {
        _floors[path] = afterVersion;
        _armed[path] = afterVersion;
    }

    /// <summary>Opens the gate: every held (and future) write for <paramref name="path"/> proceeds.</summary>
    public void Release(string path)
    {
        _armed.TryRemove(path, out _);
        _floors.TryRemove(path, out _);
        if (_gates.TryRemove(path, out var gate))
        {
            gate.OnNext(Unit.Default);
            gate.OnCompleted();
        }
    }

    public IObservable<DataChangeNotification> Changes => inner.Changes;

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => inner.Read(path, options);

    public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => inner.ReadMany(paths, options);

    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            var path = node.Path;
            if (string.IsNullOrEmpty(path) || !_armed.TryGetValue(path, out var mark))
                return WriteThrough(node, options);

            // Below the floor: the create pipeline's own seed-revision echo — not under test.
            if (_floors.TryGetValue(path, out var floor) && node.Version <= floor)
                return WriteThrough(node, options);

            if (node.Version > mark)
            {
                _armed.AddOrUpdate(path, node.Version, (_, current) => Math.Max(current, node.Version));
                return WriteThrough(node, options);
            }

            var gate = _gates.GetOrAdd(path, _ => new AsyncSubject<Unit>());
            _heldWrites.OnNext(node);
            return gate.SelectMany(_ => WriteThrough(node, options));
        });

    private IObservable<MeshNode?> WriteThrough(MeshNode node, JsonSerializerOptions options)
    {
        _writes.OnNext(node);
        return inner.Write(node, options);
    }

    public IObservable<IReadOnlyList<MeshNode>> WriteMany(
        IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            foreach (var node in nodes)
                _writes.OnNext(node);
            return inner.WriteMany(nodes, options);
        });

    public IObservable<string> Delete(string path) => inner.Delete(path);

    public IObservable<bool> DeleteIfExists(string path) => inner.DeleteIfExists(path);

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => inner.ListChildPaths(parentPath);

    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => inner.ListDescendantPaths(rootPath);

    public IObservable<bool> Exists(string path) => inner.Exists(path);

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => inner.FindBestPrefixMatch(fullPath, options);

    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => inner.ResolvePath(fullPath, options);

    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => inner.ListPartitionSubPaths(nodePath);

    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => inner.GetPartitionObjects(nodePath, subPath, options);

    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => inner.SavePartitionObjects(nodePath, subPath, objects, options);

    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => inner.DeletePartitionObjects(nodePath, subPath);

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => inner.GetPartitionMaxTimestamp(nodePath, subPath);
}
