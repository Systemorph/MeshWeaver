using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The I/O half of <see cref="BundleKeyedHold"/>: what this instance's bundles carry, what the
/// partition holds now, and what its NodeTypes' records say — read once per import, then handed to
/// the pure decision (MeshWeaver#3845 hole 4).
///
/// <para>🚨 <b>Nothing is read that cannot change the answer.</b> A deployment with no bundle shelf
/// at all exits before any query: its types compile here, so holding their sources would wait for a
/// publication that is never coming — hole 1's adjudication, one level down. A partition with no
/// ADOPTED NodeType exits before the inventory is read, which is the ordinary case for a course or a
/// document tree.</para>
///
/// <para>The share is read on the FileSystem <see cref="IIoPool"/>, the way every other reader of the
/// published root reads it (<c>PublicationSealArrivalService</c>,
/// <c>InstanceAutoRegistrationService.ProvenRef</c>): a read off the pool is invisible to the
/// registry's teardown drain. A mesh with a shelf but no pool registry is not composed the way this
/// needs, and the honest answer is to hold nothing and say so rather than to run untracked I/O.</para>
/// </summary>
internal static class BundleKeyedHoldReading
{
    /// <summary>
    /// Reads and decides. Cold; emits exactly once and never faults — a reading that fails yields
    /// <see cref="BundleHoldDecision.Nothing"/> with the reason logged, because a hold taken from a
    /// measurement that was not made is a hold nothing can release.
    /// </summary>
    /// <param name="hub">The hub whose services, configuration and workspace are read.</param>
    /// <param name="partition">The Space the import writes.</param>
    /// <param name="incoming">The parsed nodes the import would write.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>The decision.</returns>
    internal static IObservable<BundleHoldDecision> Decide(
        IMessageHub hub, string partition, IReadOnlyList<MeshNode> incoming, ILogger? logger)
        => Observable.Defer(() =>
        {
            var configuration = hub.ServiceProvider.GetService<IConfiguration>();
            var publishedRoot = configuration?[ShippedPrebuiltBundles.PublishedRootConfigKey];
            var imageDirectory = configuration?[ShippedPrebuiltBundles.DirectoryConfigKey]
                is { Length: > 0 } configured
                ? configured
                : ShippedPrebuiltBundles.DefaultDirectory;
            var identity = PrebuiltAssemblySeeder.LiveFrameworkMvid;
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            // 🚨 THE SHELF DECIDES WHETHER TO ASK THE MESH AT ALL, and it is asked ON THE POOL
            // (reviews on #4595 and #4605). Two wrong shapes were tried first and both are recorded
            // here because each looks right: a `Directory.Exists` inline blocks whichever hub turn
            // subscribed the import on a mounted share, and a CONFIGURATION-only gate is VACUOUS —
            // `imageDirectory` falls back to `ShippedPrebuiltBundles.DefaultDirectory`, so a portal
            // with no bundles at all took the mesh-wide NodeType listing plus one stream read per
            // type on EVERY import before the pooled read answered NotConfigured. `Presence` is the
            // cheap half of the read itself (two `Directory.Exists`, same definition, so the two can
            // never disagree), and nothing below runs when there is no shelf to keep in step with.
            return Presence(hub, imageDirectory, publishedRoot, identity, logger)
                .Take(1)
                .SelectMany(presence => presence is SealedReadOutcome.Read
                    ? Judge(hub, meshService, partition, incoming, imageDirectory, publishedRoot,
                        identity, logger)
                    : Abstain(presence, partition, identity, logger));
        })
        .Catch((Exception exception) =>
        {
            // A reading that faulted holds NOTHING, deliberately: an import that cannot ask the
            // question behaves exactly as it did before this gate existed, and the fault is stated.
            // The opposite direction — hold on an unreadable reading — would freeze a Space on an
            // I/O blip with nothing to release it, because the release predicate reads the same shelf.
            logger?.LogWarning(exception,
                "[BundleHold] {Partition}: the bundle-keyed hold could not be decided — this import "
                + "writes what it fetched, exactly as it did before MeshWeaver#3845 hole 4", partition);
            return Observable.Return(BundleHoldDecision.Nothing);
        });

    /// <summary>
    /// 🚨 Why a non-<see cref="SealedReadOutcome.Read"/> shelf holds NOTHING rather than everything,
    /// said once where it is decided (review on #4595): the predicate that RELEASES a bundle hold
    /// reads the same shelf, so a hold taken from a shelf that cannot be read has nothing able to
    /// clear it. `NotConfigured` is the ordinary case — every NodeType compiles here.
    /// </summary>
    /// <param name="presence">What the shelf reading can be.</param>
    /// <param name="partition">The Space the import writes.</param>
    /// <param name="identity">This instance's framework identity.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>The empty decision.</returns>
    private static IObservable<BundleHoldDecision> Abstain(
        SealedReadOutcome presence, string partition, string identity, ILogger? logger)
    {
        if (presence is SealedReadOutcome.Unreadable)
            logger?.LogWarning(
                "[BundleHold] {Partition}: the bundle shelf for framework identity {Identity} cannot "
                + "be read, so no NodeType is held — a hold taken from an unreadable shelf could not "
                + "be released by the same shelf. This import writes what it fetched; a type whose "
                + "sources move past its bundle reports StaleAdopted until the shelf is readable "
                + "again", partition, identity);
        return Observable.Return(BundleHoldDecision.Nothing);
    }

    /// <summary>
    /// The mesh half: which NodeTypes this partition has, what each one's live definition says, and
    /// the fold over its current Code nodes — read only once the shelf is known to be readable.
    /// </summary>
    /// <param name="hub">The hub whose workspace is read.</param>
    /// <param name="meshService">The query surface.</param>
    /// <param name="partition">The Space the import writes.</param>
    /// <param name="incoming">The parsed nodes the import would write.</param>
    /// <param name="imageDirectory">The image's shipped bundles.</param>
    /// <param name="publishedRoot">The published bundle root, or null.</param>
    /// <param name="identity">This instance's framework identity.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>The decision.</returns>
    private static IObservable<BundleHoldDecision> Judge(
        IMessageHub hub, IMeshService meshService, string partition,
        IReadOnlyList<MeshNode> incoming, string imageDirectory, string? publishedRoot,
        string identity, ILogger? logger)
        // 🚨 .Complete() — an ENUMERATION that gates a decision, not a search. A NodeType this
        // listing does not return is simply not judged (the safe direction), but an unpinned read
        // that inherits a bound from anywhere would silently turn "every type" into "the first
        // page" — the same declaration the importer's own prune snapshot makes.
        // 🚨 The RESIDUAL, stated where the assumption is made (review on #4595): this listing is
        // the read model's, and the read model is eventually consistent. A NodeType it does not
        // return is not judged, so its sources move as they did before this gate and the type can
        // report StaleAdopted — the pre-#3845 behaviour, never a new harm, and self-healing on the
        // next import. The alternative — refusing to import until completeness is PROVEN — cannot be
        // built on this instrument: `.Complete()` pins the read against a paging limit (which is what
        // silently turns "every type" into "the first page"), and nothing in the mesh offers an
        // authoritative enumeration of a partition. The importer's own prune snapshot declares the
        // same read for the same reason.
        => meshService
                .Query<MeshNode>(MeshQueryRequest
                    .FromQuery($"path:{partition} scope:descendants nodeType:{MeshNode.NodeTypePath}")
                    .Complete())
                .Take(1)
                .SelectMany(types =>
                {
                    var typePaths = types.Items
                        .Select(n => n.Path)
                        .Where(p => !string.IsNullOrEmpty(p))
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .ToImmutableArray();
                    if (typePaths.IsEmpty)
                        return Observable.Return(BundleHoldDecision.Nothing);
                    // 🚨 The DEFINITIONS come from each node's own stream, never from the listing
                    // above: this decides whether content moves, and a query answer can be minutes
                    // old (CQRS). The listing answers EXISTENCE, the stream answers CONTENT.
                    return Observable
                        .Zip(typePaths.Select(path => hub.GetWorkspace().GetMeshNodeStream(path)
                            .Take(1)
                            .Select(node => (Path: path,
                                Definition: node?.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions, logger)))))
                        .Take(1)
                        .SelectMany(definitions =>
                        {
                            var live = definitions
                                .Where(d => d.Definition is not null)
                                .ToImmutableDictionary(d => d.Path, d => d.Definition!, StringComparer.Ordinal);
                            // Only a VERIFIED adoption can be held — so a partition with none needs
                            // neither the inventory nor the partition's code nodes.
                            if (!live.Values.Any(d => d.BuildProvenance is BuildProvenance.AdoptedVerified))
                                return Observable.Return(BundleHoldDecision.Nothing);
                            return Inventory(hub, imageDirectory, publishedRoot, identity, logger)
                                // 🚨 `is not Read`, not `is NotConfigured`: the shelf was present at
                                // the pre-check and an ARCHIVE under it could still be unreadable, and
                                // that abstains for the same reason (review on #4605) — through the
                                // same sentence, so the two paths cannot drift apart.
                                .SelectMany(inventory => inventory.Outcome is not SealedReadOutcome.Read
                                    ? Abstain(inventory.Outcome, partition, identity, logger)
                                    : Current(meshService, partition).Select(current => BundleKeyedHold.Decide(
                                        partition, incoming, current, live,
                                        node => node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions, logger),
                                        inventory, identity, logger)));
                        });
                });

    /// <summary>The partition's Code nodes — the compile inputs the fingerprints fold over. A
    /// content-bearing enumeration, pinned like the type listing.</summary>
    private static IObservable<IReadOnlyList<MeshNode>> Current(IMeshService meshService, string partition)
        => meshService
            .Query<MeshNode>(MeshQueryRequest
                .FromQuery($"path:{partition} scope:descendants nodeType:Code")
                .Complete())
            .Take(1)
            .Select(change => (IReadOnlyList<MeshNode>)change.Items);

    /// <summary>Whether a shelf is there at all, on the FileSystem pool — the cheap pre-check that
    /// keeps the mesh-wide listing off an instance that consumes no bundles. No pool registry means
    /// the same answer as <see cref="Inventory"/> gives: nothing is read, and nothing is held.</summary>
    /// <param name="hub">The hub whose pool registry is resolved.</param>
    /// <param name="imageDirectory">The image's shipped bundles.</param>
    /// <param name="publishedRoot">The published bundle root, or null.</param>
    /// <param name="identity">This instance's framework identity.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>What a full read could be.</returns>
    private static IObservable<SealedReadOutcome> Presence(
        IMessageHub hub, string imageDirectory, string? publishedRoot, string identity, ILogger? logger)
        => hub.ServiceProvider.GetService<IoPoolRegistry>() is { } pools
            ? pools.Get(IoPoolNames.FileSystem)
                .InvokeBlocking(_ => PrebuiltBundleInventory.Presence(
                    imageDirectory, publishedRoot, identity, logger))
            : Observable.Return(SealedReadOutcome.NotConfigured);

    /// <summary>The bundle inventory, read on the FileSystem pool. A mesh with a shelf but no pool
    /// registry reads nothing and says so — untracked share I/O is the straggler the pool exists to
    /// prevent.</summary>
    private static IObservable<PrebuiltBundleInventory> Inventory(
        IMessageHub hub, string imageDirectory, string? publishedRoot, string identity, ILogger? logger)
    {
        if (hub.ServiceProvider.GetService<IoPoolRegistry>() is not { } pools)
        {
            logger?.LogWarning(
                "[BundleHold] no IoPoolRegistry is registered, so the bundle inventory under {Root} "
                + "cannot be read on a drained pool — not reading it; this import writes what it "
                + "fetched", publishedRoot ?? imageDirectory);
            return Observable.Return(PrebuiltBundleInventory.NotConfigured);
        }
        return pools.Get(IoPoolNames.FileSystem)
            .InvokeBlocking(_ => PrebuiltBundleInventory.Read(
                imageDirectory, publishedRoot, identity, logger));
    }
}
