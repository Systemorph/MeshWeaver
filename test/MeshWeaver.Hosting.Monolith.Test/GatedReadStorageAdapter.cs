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
/// Test-only innermost <see cref="IStorageAdapter"/> decorator that makes <see cref="Read"/>
/// GENUINELY ASYNCHRONOUS for designated paths — the deterministic stand-in for a Postgres/blob
/// backend whose activation-seed read lags the synchronously-replaying routing caches. This is
/// what turns the per-node-hub activation races (#590/#648/#667 — stale seed, superseded
/// activation, deploy-window write loss) from CI-load flakes into deterministic repros: hold the
/// path's reads, drive the scenario, observe that the read was reached, release.
///
/// <para>Register it BEFORE the test base's <c>AddInMemoryPersistence</c> (whose
/// <c>TryAddSingleton</c> then no-ops) so the framework's write-integrity chain
/// (SubtreeDeletionGuard → MonotonicWriteGuard → VersionWriting) decorates THIS adapter —
/// the gate then sits exactly where a slow backend sits, underneath every guard.</para>
///
/// <para>Instance state only (mesh-scoped singleton, dies with the test mesh) — no statics.</para>
/// </summary>
internal sealed class GatedReadStorageAdapter(IStorageAdapter inner) : IStorageAdapter
{
    private readonly ConcurrentDictionary<string, AsyncSubject<Unit>> _gates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ReplaySubject<Unit>> _heldReadObserved =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _writeCounts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Holds every subsequent <see cref="Read"/> of <paramref name="path"/> until
    /// <see cref="Release"/>. Idempotent per path.</summary>
    public void HoldReads(string path)
        => _gates.GetOrAdd(path, _ => new AsyncSubject<Unit>());

    /// <summary>Opens the gate: held (and future) reads of <paramref name="path"/> proceed.</summary>
    public void Release(string path)
    {
        if (_gates.TryRemove(path, out var gate))
        {
            gate.OnNext(Unit.Default);
            gate.OnCompleted();
        }
    }

    /// <summary>
    /// Emits once a <see cref="Read"/> for <paramref name="path"/> has ARRIVED at a closed gate —
    /// the positive "the activation/consumer reached its durable read and is held" signal tests
    /// sequence on instead of sleeping. Replayed, so subscribing after the arrival still fires.
    /// </summary>
    public IObservable<Unit> WaitForHeldRead(string path)
        => _heldReadObserved.GetOrAdd(path, _ => new ReplaySubject<Unit>(1)).Take(1);

    /// <summary>Number of storage writes that REACHED this (innermost) adapter for
    /// <paramref name="path"/> — refused writes never arrive here.</summary>
    public int WriteCount(string path) => _writeCounts.GetValueOrDefault(path);

    public IObservable<DataChangeNotification> Changes => inner.Changes;

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            if (_gates.TryGetValue(path, out var gate))
            {
                _heldReadObserved.GetOrAdd(path, _ => new ReplaySubject<Unit>(1))
                    .OnNext(Unit.Default);
                return gate.SelectMany(_ => inner.Read(path, options));
            }
            return inner.Read(path, options);
        });

    public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => paths.Select(p => Read(p, options).Where(n => n is not null).Select(n => n!)).Merge();

    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            if (!string.IsNullOrEmpty(node.Path))
                _writeCounts.AddOrUpdate(node.Path, 1, (_, c) => c + 1);
            return inner.Write(node, options);
        });

    public IObservable<IReadOnlyList<MeshNode>> WriteMany(
        IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            foreach (var node in nodes)
                if (!string.IsNullOrEmpty(node.Path))
                    _writeCounts.AddOrUpdate(node.Path, 1, (_, c) => c + 1);
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
