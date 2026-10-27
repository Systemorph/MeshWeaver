using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Writes a compile's forward dependency edges (<c>Doc/Architecture/DependencyGraph</c>) as ONE
/// node, <c>{NodeTypePath}/_Dependency/Forward</c>, replaced whole — so a recompile that drops an
/// edge drops it in the same write that adds the new ones, and no reader ever needs a query to
/// find which edges to delete.
///
/// <para>Framework infrastructure, written as System (the compile itself runs as System; the
/// edges describe the type, not anybody's request). The content is the complete set, never a fold
/// over the previous one — and a set of an older generation never replaces a newer one
/// (<see cref="Write"/>).</para>
/// </summary>
internal static class DependencyEdgeSetWriter
{
    /// <summary>The node that stores <paramref name="edges"/>.</summary>
    /// <param name="edges">The edge set, already stamped with the build it was read from.</param>
    internal static MeshNode NodeFor(DependencyEdgeSet edges) =>
        new(DependencyEdgeSetPaths.ForwardId, DependencyEdgeSetPaths.NamespaceFor(edges.Source))
        {
            Name = "Forward dependencies",
            NodeType = DependencyEdgeSetPaths.NodeType,
            State = MeshNodeState.Active,
            Content = edges,
        };

    /// <summary>How often a write that lost its compare-and-set re-reads and tries again. Each
    /// retry follows a write that LANDED meanwhile, and every writer of one source converges on the
    /// highest generation, so this bounds a burst rather than a livelock.</summary>
    internal const int MaxAttempts = 5;

    /// <summary>
    /// Whether <paramref name="stored"/> is of a NEWER generation than <paramref name="incoming"/>
    /// (<see cref="DependencyEdgeSet.StampVersion"/>), so writing <paramref name="incoming"/> would
    /// put an older build's edges over a newer build's. An incoming set with no generation never
    /// replaces one that has one; equal generations re-write (the same stamp, idempotent).
    /// </summary>
    /// <param name="stored">The set currently stored, or null.</param>
    /// <param name="incoming">The set about to be written.</param>
    internal static bool IsSupersededBy(DependencyEdgeSet? stored, DependencyEdgeSet incoming) =>
        stored?.StampVersion is { } storedGeneration
        && (incoming.StampVersion is not { } incomingGeneration || storedGeneration > incomingGeneration);

    /// <summary>
    /// Writes <paramref name="edges"/>. Cold. A failure is reported at Error naming the type and
    /// completes the observable: the compile it follows has already landed, and an edge set that
    /// did not land reads as "not extracted for this build" (its <see cref="DependencyEdgeSet.BuildMvid"/>
    /// is absent or older), never as "depends on nothing".
    ///
    /// <para>🚨 <b>The newest generation wins, whatever order the writes arrive in.</b> Two writers
    /// publish edges for one source — the activation's compile settle and the cold-start batch bake
    /// — and each writes AFTER its own stamp landed. An older compile can land its stamp, pause, and
    /// arrive with its edges after a newer compile has stamped and written. So the write is a
    /// compare-and-set on the edge-set row (<see cref="IStorageAdapter.WriteIfVersion"/>, the same
    /// primitive the batch stamp uses) guarded by <see cref="DependencyEdgeSet.StampVersion"/>: read
    /// the stored set, skip when it is of a newer generation, otherwise write conditioned on the
    /// row version just read. A refused write means another writer landed in between; it re-reads
    /// and decides again. Without a storage adapter (a host with no persistence) it falls back to
    /// a plain <c>CreateOrUpdateNode</c>.</para>
    /// </summary>
    /// <param name="hub">A hub that outlives the compile's own activation (the settle hub).</param>
    /// <param name="edges">The edge set to store, stamped with its build and generation.</param>
    /// <param name="logger">Diagnostics.</param>
    internal static IObservable<Unit> Write(IMessageHub hub, DependencyEdgeSet edges, ILogger? logger)
    {
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        var write = storage is null
            ? WriteThroughMesh(hub, edges)
            : Observable.Defer(() => CompareAndSet(hub, storage, edges, logger, attempt: 1));
        return write
            .Take(1)
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogError(ex,
                    "Writing the dependency edges of {Source} (build {BuildMvid}) failed — the "
                    + "dependency graph has no edge set for this build", edges.Source, edges.BuildMvid);
                return Observable.Empty<Unit>();
            });
    }

    private static IObservable<Unit> WriteThroughMesh(IMessageHub hub, DependencyEdgeSet edges)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => meshService.CreateOrUpdateNode(NodeFor(edges)))
            .Select(_ => Unit.Default);
    }

    private static IObservable<Unit> CompareAndSet(
        IMessageHub hub, IStorageAdapter storage, DependencyEdgeSet edges, ILogger? logger, int attempt)
    {
        var options = hub.JsonSerializerOptions;
        var path = DependencyEdgeSetPaths.PathFor(edges.Source);
        return storage.Read(path, options)
            .Take(1)
            .SelectMany(stored =>
            {
                if (IsSupersededBy(stored?.ContentAs<DependencyEdgeSet>(options, logger), edges))
                {
                    logger?.LogDebug(
                        "Dependency edges of {Source} (build {BuildMvid}, generation {Generation}) not written: "
                        + "a newer generation is already stored", edges.Source, edges.BuildMvid, edges.StampVersion);
                    return Observable.Return(Unit.Default);
                }
                var expected = stored?.Version ?? 0;
                var node = NodeFor(edges) with { Version = MeshNode.NextVersion(expected) };
                return storage.WriteIfVersion(node, expected, options)
                    .Take(1)
                    .SelectMany(applied =>
                    {
                        if (applied is true)
                        {
                            // Post-commit announce, as the batch stamp does: the change feed is what
                            // invalidates the resolution and stream caches for a storage-level write.
                            hub.ServiceProvider.GetService<IMeshChangeFeed>()?.Publish(stored is null
                                ? MeshChangeEvent.Created(node)
                                : MeshChangeEvent.Updated(node));
                            return Observable.Return(Unit.Default);
                        }
                        if (applied is false && attempt < MaxAttempts)
                            return CompareAndSet(hub, storage, edges, logger, attempt + 1);
                        logger?.LogError(
                            "Writing the dependency edges of {Source} (build {BuildMvid}, generation {Generation}) "
                            + "did not land after {Attempts} attempt(s) ({Outcome}) — the dependency graph has no "
                            + "edge set for this build", edges.Source, edges.BuildMvid, edges.StampVersion, attempt,
                            applied is false ? "the row kept moving" : "the store wrote nothing");
                        return Observable.Return(Unit.Default);
                    });
            });
    }
}
