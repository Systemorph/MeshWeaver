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
            // 🚨 NO FILESYSTEM PROBE HERE (review on #4595). Whether a directory exists is I/O, and
            // this runs on whichever hub turn subscribed the import; the published root is a mounted
            // share, so a probe of it can block that turn. The only question answered off the pool is
            // one about CONFIGURATION — and when neither key names a shelf there is nothing to keep
            // in step, because every type compiles here. Everything else is decided by the pooled
            // read below, which answers NotConfigured when nothing is on disk.
            if (string.IsNullOrWhiteSpace(publishedRoot) && string.IsNullOrWhiteSpace(imageDirectory))
                return Observable.Return(BundleHoldDecision.Nothing);

            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            // 🚨 .Complete() — an ENUMERATION that gates a decision, not a search. A NodeType this
            // listing does not return is simply not judged (the safe direction), but an unpinned read
            // that inherits a bound from anywhere would silently turn "every type" into "the first
            // page" — the same declaration the importer's own prune snapshot makes.
            // 🚨 The RESIDUAL, stated where the assumption is made (review on #4595): this listing is
            // the read model's, and the read model is eventually consistent. A NodeType it does not
            // return is not judged, so its sources move as they did before this gate and the type can
            // report StaleAdopted — the pre-#3845 behaviour, never a new harm, and self-healing on
            // the next import. The alternative — refusing to import until completeness is PROVEN —
            // cannot be built on this instrument: `.Complete()` pins the read against a paging limit
            // (which is what silently turns "every type" into "the first page"), and nothing in the
            // mesh offers an authoritative enumeration of a partition. The importer's own prune
            // snapshot declares the same read for the same reason.
            return meshService
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
                                .SelectMany(inventory => inventory.Outcome is SealedReadOutcome.NotConfigured
                                    ? Observable.Return(BundleHoldDecision.Nothing)
                                    : Current(meshService, partition).Select(current => BundleKeyedHold.Decide(
                                        partition, incoming, current, live,
                                        node => node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions, logger),
                                        inventory, identity, logger)));
                        });
                });
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

    /// <summary>The partition's Code nodes — the compile inputs the fingerprints fold over. A
    /// content-bearing enumeration, pinned like the type listing.</summary>
    private static IObservable<IReadOnlyList<MeshNode>> Current(IMeshService meshService, string partition)
        => meshService
            .Query<MeshNode>(MeshQueryRequest
                .FromQuery($"path:{partition} scope:descendants nodeType:Code")
                .Complete())
            .Take(1)
            .Select(change => (IReadOnlyList<MeshNode>)change.Items);

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
