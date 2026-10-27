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
            // A satellite of the NodeType, never a main node: it must not answer `is:main` or list
            // as an instance.
            MainNode = edges.Source,
            NodeType = DependencyEdgeSetPaths.NodeType,
            State = MeshNodeState.Active,
            Content = edges,
        };

    /// <summary>
    /// The set the activation's settle publishes after its stamp update: <paramref name="edges"/>
    /// under the generation the update landed at — or nothing, when there are no edges or the
    /// update did not write this build's success stamp (the guarded no-op returns the node
    /// unchanged, and its version belongs to whatever the node holds now, not to this build).
    /// </summary>
    /// <param name="edges">The compile's edges, or null.</param>
    /// <param name="successStamped">Whether the update wrote this build's success stamp.</param>
    /// <param name="saved">The node the update emitted.</param>
    internal static DependencyEdgeSet? ToPublish(DependencyEdgeSet? edges, bool successStamped, MeshNode saved) =>
        edges is not null && successStamped ? edges with { StampVersion = saved.Version } : null;

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
    /// The tie: <paramref name="stored"/> and <paramref name="incoming"/> carry the SAME generation
    /// but describe different builds. Both stamp writers can mint one version for two builds (the
    /// activation acknowledges v+1 in memory before its flush, the batch bake writes v+1 from the
    /// durable v), so the generation alone cannot order them; the source's own record decides.
    /// </summary>
    /// <param name="stored">The set currently stored, or null.</param>
    /// <param name="incoming">The set about to be written.</param>
    internal static bool IsTie(DependencyEdgeSet? stored, DependencyEdgeSet incoming) =>
        stored?.StampVersion is { } storedGeneration
        && incoming.StampVersion == storedGeneration
        && !string.Equals(stored.BuildMvid, incoming.BuildMvid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the source's durable record <paramref name="record"/> names a build other than
    /// <paramref name="incoming"/>'s. A record that names no build (an in-memory mesh) does not
    /// contradict anything.
    /// </summary>
    /// <param name="record">The source NodeType's record as stored, or null.</param>
    /// <param name="incoming">The set about to be written.</param>
    internal static bool RecordNamesAnotherBuild(NodeTypeDefinition? record, DependencyEdgeSet incoming) =>
        record?.LatestAssemblyMvid is { Length: > 0 } recordBuild
        && !string.Equals(recordBuild, incoming.BuildMvid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="stored"/> already describes <paramref name="incoming"/>'s build with
    /// the same edges AT A GENERATION NO OLDER than <paramref name="incoming"/>'s — a rewrite would
    /// change nothing but the row's version. Makes every writer idempotent, which the adoption
    /// writer needs: it runs on every bundle pass of every boot.
    ///
    /// <para>🚨 The generation is part of the identity. Same bytes and edges under a NEWER
    /// <see cref="DependencyEdgeSet.StampVersion"/> must still be written: it is what lets
    /// <see cref="IsSupersededBy"/> refuse an older, delayed writer. Skipping it would leave the
    /// old generation stored, and a delayed write of an intermediate build (a generation between
    /// the two) would then replace this build's edges with its own.</para>
    /// </summary>
    /// <param name="stored">The set currently stored, or null.</param>
    /// <param name="incoming">The set about to be written.</param>
    internal static bool DescribesSameBuild(DependencyEdgeSet? stored, DependencyEdgeSet incoming) =>
        stored is not null
        && incoming.BuildMvid is { Length: > 0 }
        && string.Equals(stored.BuildMvid, incoming.BuildMvid, StringComparison.OrdinalIgnoreCase)
        && !IsNewerGeneration(incoming, stored)
        && stored.Edges.Select(e => (e.Kind, e.Target)).SequenceEqual(incoming.Edges.Select(e => (e.Kind, e.Target)))
        && stored.IncludedWithoutUse.SequenceEqual(incoming.IncludedWithoutUse);

    /// <summary>Whether <paramref name="incoming"/> carries a generation the stored set does not
    /// reach: a generation where the stored set has none, or a higher one.</summary>
    private static bool IsNewerGeneration(DependencyEdgeSet incoming, DependencyEdgeSet stored) =>
        incoming.StampVersion is { } incomingGeneration
        && (stored.StampVersion is not { } storedGeneration || incomingGeneration > storedGeneration);

    /// <summary>
    /// Writes the edges a prebuilt bundle CARRIED for bytes this mesh adopted
    /// (<c>Doc/Architecture/DependencyGraph</c> → adopted prebuilts). An adoption compiles nothing,
    /// so without this an adopting mesh has no edge set for the build it serves. Written only while
    /// the source's durable record NAMES the bundle's build (<see cref="NodeTypeDefinition.LatestAssemblyMvid"/>
    /// = the set's <see cref="DependencyEdgeSet.BuildMvid"/>), under that record's version as the
    /// generation — so a later local compile (a higher stamp) always wins, and an adoption the owner
    /// refused (the record names another build) writes nothing. Idempotent across boots
    /// (<see cref="DescribesSameBuild"/>). Without a storage adapter there is no record to check
    /// against, so nothing is written.
    /// </summary>
    /// <param name="hub">The seeding hub.</param>
    /// <param name="edges">The bundle's set for the adopted bytes.</param>
    /// <param name="logger">Diagnostics.</param>
    internal static IObservable<Unit> WriteAdopted(IMessageHub hub, DependencyEdgeSet edges, ILogger? logger)
    {
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null || edges.BuildMvid is not { Length: > 0 })
            return Observable.Empty<Unit>();
        var options = hub.JsonSerializerOptions;
        return storage.Read(edges.Source, options)
            .Take(1)
            .SelectMany(record =>
            {
                var definition = record?.ContentAs<NodeTypeDefinition>(options, logger);
                if (record is null || definition?.LatestAssemblyMvid is not { Length: > 0 } served
                    || !string.Equals(served, edges.BuildMvid, StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogDebug(
                        "Adopted dependency edges of {Source} (build {BuildMvid}) not written: the record "
                        + "serves {Served}", edges.Source, edges.BuildMvid, definition?.LatestAssemblyMvid ?? "no build");
                    return Observable.Empty<Unit>();
                }
                return Write(hub, edges with { StampVersion = record.Version }, logger);
            })
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogError(ex,
                    "Writing the adopted dependency edges of {Source} (build {BuildMvid}) failed", edges.Source, edges.BuildMvid);
                return Observable.Empty<Unit>();
            });
    }

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
                var storedSet = stored?.ContentAs<DependencyEdgeSet>(options, logger);
                if (DescribesSameBuild(storedSet, edges))
                    return Observable.Return(Unit.Default);
                if (IsSupersededBy(storedSet, edges))
                {
                    logger?.LogDebug(
                        "Dependency edges of {Source} (build {BuildMvid}, generation {Generation}) not written: "
                        + "a newer generation is already stored", edges.Source, edges.BuildMvid, edges.StampVersion);
                    return Observable.Return(Unit.Default);
                }
                var expected = stored?.Version ?? 0;
                if (!IsTie(storedSet, edges))
                    return Publish(stored, expected);
                // A tie: read the source's record AFTER the edge row, so a stamp landing in between
                // either shows here or moves the edge row and refuses the compare-and-set below.
                return storage.Read(edges.Source, options)
                    .Take(1)
                    .SelectMany(record =>
                    {
                        if (RecordNamesAnotherBuild(record?.ContentAs<NodeTypeDefinition>(options, logger), edges))
                        {
                            logger?.LogDebug(
                                "Dependency edges of {Source} (build {BuildMvid}) not written: the stored set has the "
                                + "same generation {Generation}, and the record names another build",
                                edges.Source, edges.BuildMvid, edges.StampVersion);
                            return Observable.Return(Unit.Default);
                        }
                        return Publish(stored, expected);
                    });
            });

        IObservable<Unit> Publish(MeshNode? stored, long expected)
        {
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
        }
    }
}
