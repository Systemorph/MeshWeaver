using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Un-pins a per-node hub whose node's <see cref="MeshNode.NodeType"/> changed after the hub was
/// activated — the fix for issue #1104, <i>"a hub activated on the wrong NodeType stays wrong"</i>.
///
/// <para><b>The defect.</b> A per-node hub binds its <c>HubConfiguration</c> from its node's
/// NodeType EXACTLY ONCE, during activation, and is then PINNED by address in
/// <c>HostedHubsCollection</c>. Routing short-circuits on <c>Mesh.GetHostedHub(…, Never)</c> for an
/// already-hosted address, so a hosted hub never resolves its path again and nothing ever re-reads
/// its NodeType. If the node acquires — or changes — its type while the hub is alive, the hub keeps
/// serving the configuration it was born with for the rest of its lifetime.</para>
///
/// <para><b>Why it bites.</b> A partition's schema is provisioned BEFORE its root node is written,
/// and <c>PathResolutionService.SynthesizePartitionRoot</c> fabricates a NodeType-less placeholder
/// so a bare partition path can still answer inside that window. Any routed touch there activates
/// the root's hub against the mesh <c>DefaultNodeHubConfiguration</c>; the installer's later retype
/// changes the row and the hub is none the wiser. That is the plugin gate's <c>Store/Catalog</c>
/// RED — "No renderer is registered for area <c>Tests</c> on hub <c>Store</c>", listing item for
/// item the framework defaults and not one area of the installed package.</para>
///
/// <para><b>Why fixing resolution was not enough.</b> #1055 stopped the fabricated placeholder from
/// being CACHED, and #815's installer posts a <see cref="DisposeRequest"/> after the retype. Both
/// are necessary; neither is sufficient. <i>Not cached is not not-pinned</i>: repairing the
/// resolution path cannot help a hub a bad resolution has ALREADY activated, and the installer's
/// recycle is fire-and-forget, conditional on the placeholder dance having run, and unavailable to
/// every other writer that retypes a node (an import, a repair migration, a user). The guarantee
/// has to live in the framework, at the hub.</para>
///
/// <para><b>The mechanism.</b> Every activation arms a watcher on the instance hub (the same
/// <c>WithInitialization</c> + self-<see cref="DisposeRequest"/> idiom as
/// <c>NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal</c> and <c>RecycleLayoutArea</c>). It observes
/// <see cref="IMeshInvalidationFeed"/> for THIS node's path and recycles the hub the first time an event
/// reports a NodeType different from the one the configuration was bound from. The hub tears down,
/// the next access re-activates it, and enrichment binds the node's real type.</para>
///
/// <para>🚨 <b>The change feed, never the hub's own node stream.</b> Both would see the retype, but
/// only the feed sees it POST-COMMIT: <c>StorageAdapterChangeFeedExtensions</c> publishes
/// Created/Updated strictly after the storage adapter's write emits. Recycling off the hub's own
/// in-memory commit would race its debounced persist — the re-activation reads the node back
/// through routing, i.e. through storage, so it would resurrect the PRE-retype node and the fresh
/// hub would bind the wrong type again (silently, and with the old row now authoritative). That is
/// the same hazard <c>PackageInstaller.RootRetypePersisted</c> exists for. Using the feed makes the
/// ordering airtight for free — it is the very event that invalidates
/// <c>PathResolutionService</c>'s resolution cache, so by the time we recycle, the resolver the
/// re-activation will consult has already been swept.</para>
///
/// <para><b>Blast radius.</b> Disposing a live hub is not free — in-flight subscriptions are torn
/// down and clients re-subscribe — so the watcher fires only on a genuine change of THIS node's
/// type, and only once (<c>Take(1)</c>); after the recycle the new hub's baseline IS the new type,
/// so the predicate cannot re-fire and there is no recycle loop. Content writes, satellite writes,
/// and writes to other paths are all no-ops for it. Arming costs one filtered subscription on the
/// process-wide feed subject — no per-hub upstream stream, no poll, no timer.</para>
///
/// <para><b>The fan-out cost, chosen deliberately.</b> One subscription per live hub means each
/// published change walks every armed watcher: an enum compare and an ordinal path compare that
/// fails on the first character for all but one of them. That is the same trade
/// <c>PathResolutionService.OnMeshChange</c> already makes (a full-dictionary sweep per event,
/// "deliberate: change events are rare relative to resolutions"), and it buys the simplicity of
/// having NO shared registry — no mesh-scoped path→watcher map to keep in step with hub lifetimes,
/// nothing to leak when a hub dies without unregistering. If the feed ever becomes hot enough for
/// this to matter, the fix is a keyed feed, not a cache here.</para>
/// </summary>
internal static class NodeTypeRebindWatcher
{
    private sealed class LegacyInvalidationFeed(IMeshChangeFeed feed) : IMeshInvalidationFeed
    {
        public IDisposable Subscribe(
            Action<MeshChangeEvent> handler,
            MeshChangeKind? filter = null)
            => feed.Subscribe(handler, filter);
    }

    /// <summary>
    /// Wraps <paramref name="enriched"/>'s HubConfiguration so the hub it activates arms the
    /// rebind watcher. The baseline is <paramref name="enriched"/>'s own NodeType — the type the
    /// configuration about to be applied was resolved from.
    /// </summary>
    public static MeshNode WithNodeTypeRebind(
        MeshNode enriched, IMessageHub meshHub, ILogger? logger)
    {
        var baseConfig = enriched.HubConfiguration;
        // 🚨 A NULL HubConfiguration MUST SURVIVE. Both activation sites branch on it to activate
        // the fail-fast NACK-FALLBACK hub — a hub whose UnhandledMessageNack answers every message
        // with a typed DeliveryFailure naming the node type, and which DeactivateOnIdle's so the
        // next access retries. Wrapping here would make it non-null unconditionally, killing that
        // branch and swapping fail-fast for a bare hub that Ignores typed requests: the PARK class
        // (senders wait out their whole budget instead of getting a diagnostic). That is the exact
        // rule NodeTypeEnrichmentHelpers.ApplyStreamResult already states for its own wrap — "only
        // wrap when a hub configuration will actually be composed". Nothing is lost: a hub with no
        // configuration at all already retries on the next access, so it is never pinned.
        if (baseConfig is null)
            return enriched;
        var path = enriched.Path;
        var boundNodeType = enriched.NodeType;
        return enriched with
        {
            HubConfiguration = config =>
                baseConfig(config)
                    .WithInitialization(instanceHub =>
                    {
                        // Fire-and-forget by design: a watcher that cannot be armed must never
                        // fault hub initialization. Worst case is the pre-fix world (the hub stays
                        // on its activation-time type until someone recycles it), never a dead hub.
                        try
                        {
                            var feed = meshHub.ServiceProvider.GetService<IMeshInvalidationFeed>();
                            if (feed is null
                                && meshHub.ServiceProvider.GetService<IMeshChangeFeed>() is { } logicalFeed)
                                feed = new LegacyInvalidationFeed(logicalFeed);
                            if (feed is null)
                                return;
                            // #3510: a recycle of a root an install is writing under waits for the
                            // install to release it. Resolved from the MESH's provider, which is
                            // where the single mesh-scoped registry lives.
                            var leases = meshHub.ServiceProvider
                                .GetService<PackageRootInstallLeases>();
                            instanceHub.RegisterForDisposal(
                                Arm(feed, instanceHub, path, boundNodeType, logger, leases));
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex,
                                "NodeType rebind: could not arm the watcher for '{Path}' (bound NodeType '{NodeType}')",
                                path, boundNodeType ?? "(none)");
                        }
                    })
        };
    }

    /// <summary>
    /// Watcher core, split out so the firing contract is testable without building a hub
    /// (exactly as <c>ArmOverlaySelfHeal</c> is). Posts at most ONE self-<see cref="DisposeRequest"/>.
    ///
    /// <para>🚨 <b>The post WAITS while an install holds this exact path</b> (#3510). This watcher
    /// is armed on every instance hub, package roots included, and <see cref="RequiresRebind"/>
    /// fires on precisely the event the installer's placeholder dance produces — the root's retype.
    /// So it can, by construction, tear a package root down in the middle of that package's own
    /// install, stranding the writes the root's per-node children owe acks for. It now defers to
    /// <see cref="PackageRootInstallLeases"/> and fires on the release instead. <c>Take(1)</c> is
    /// applied BEFORE the wait, so a flapping writer still cannot turn this into a recycle storm —
    /// the first qualifying event latches and the rest are never seen.</para>
    ///
    /// <para>The wait is scoped to the path itself, never its subtree: an install waits on the
    /// rebuilds of the NodeTypes BENEATH its root, and deferring the per-type recyclers that serve
    /// those rebuilds against the install waiting for them would be a deadlock.</para>
    /// </summary>
    /// <param name="feed">The post-commit mesh change feed.</param>
    /// <param name="instanceHub">The hub to recycle.</param>
    /// <param name="path">The node path this hub serves.</param>
    /// <param name="boundNodeType">The NodeType its configuration was resolved from.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="leases">The mesh's install-lease registry, or null on a host that registers
    /// none — in which case no lease can exist and the recycle posts as it always did.</param>
    public static IDisposable Arm(
        IMeshInvalidationFeed feed,
        IMessageHub instanceHub,
        string path,
        string? boundNodeType,
        ILogger? logger,
        PackageRootInstallLeases? leases = null)
        => Observable.Create<MeshChangeEvent>(observer => feed.Subscribe(observer.OnNext))
            .Where(change => RequiresRebind(change, path, boundNodeType))
            .Take(1)
            .SelectMany(change => WaitWhileAnInstallHoldsIt(leases, path, logger)
                .Select(_ => change))
            .Subscribe(
                change =>
                {
                    // 🚨 The handler runs SYNCHRONOUSLY on the PUBLISHER's thread — inside the
                    // storage write's post-commit Do (StorageAdapterChangeFeedExtensions). A throw
                    // here would propagate INTO that write and fail an unrelated caller's save, so
                    // this recycle can never be allowed to escape. It is cheap and non-blocking:
                    // Post enqueues onto the target's action block, and DisposeRequest is
                    // [SystemMessage]/[CanBeIgnored], so it needs no AccessContext on a thread that
                    // carries none.
                    try
                    {
                        // A hub already tearing down needs no recycle, and posting to it would only
                        // add a delivery the disposal has to drain.
                        if (instanceHub.IsDisposing)
                            return;
                        logger?.LogInformation(
                            "NodeType rebind: node '{Path}' is now typed '{NewNodeType}' but its hub activated on "
                            + "'{BoundNodeType}' — recycling so the next access binds the real type",
                            path, change.NodeType ?? "(none)", boundNodeType ?? "(none)");
                        // 🚨 The reason rides along (#3510). This watcher was that issue's LEADING
                        // hypothesis for six occurrences precisely because a self-posted
                        // DisposeRequest renders as "requested by itself — a rebind or self-heal
                        // recycle": one word covering this watcher, the stale-build convergence and
                        // the overlay self-heal. Naming it here is what turns the next occurrence
                        // into a read instead of an ordering argument.
                        instanceHub.Post(
                            new DisposeRequest
                            {
                                Reason = $"NodeType rebind: node '{path}' is now typed "
                                         + $"'{change.NodeType ?? "(none)"}' but its hub activated on "
                                         + $"'{boundNodeType ?? "(none)"}'",
                            },
                            o => o.WithTarget(instanceHub.Address));
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex,
                            "NodeType rebind: recycling '{Path}' after its retype failed — the hub keeps its "
                            + "activation-time configuration until it is recycled", path);
                    }
                },
                ex => logger?.LogWarning(ex,
                    "NodeType rebind watcher for '{Path}' faulted — the hub keeps its activation-time "
                    + "configuration until it is recycled", path));

    /// <summary>
    /// Emits once no install is writing under <paramref name="path"/> — at once in the ordinary
    /// case (a retype by an import, a repair migration or a user, with nothing installing), and on
    /// the install's release when one holds it.
    ///
    /// <para>Announced in both directions, for the same reason <c>HubRecycleExtensions</c> does it:
    /// a recycle that silently waits reads exactly like a recycle that was never asked for, and a
    /// lease that outlived its install would otherwise be invisible.</para>
    /// </summary>
    private static IObservable<Unit> WaitWhileAnInstallHoldsIt(
        PackageRootInstallLeases? leases, string path, ILogger? logger)
        => Observable.Defer(() =>
        {
            if (leases?.HeldBy(path) is not { } holder)
                return Observable.Return(Unit.Default);
            logger?.LogInformation(
                "NodeType rebind: deferring the recycle of '{Path}' — an install is writing under "
                + "that root ({Holder}), and recycling it now would strand the writes its per-node "
                + "children owe acks for (#3510). The recycle runs when the install releases it",
                path, holder);
            return leases.WhenReleased(path)
                .Do(_ => logger?.LogInformation(
                    "NodeType rebind: the install released '{Path}' — proceeding with the deferred "
                    + "recycle", path));
        });

    /// <summary>
    /// The firing predicate: a post-commit Created/Updated event for THIS node whose NodeType is
    /// not the one the hub bound.
    ///
    /// <para>Deletes are excluded — a deleted node's hub is torn down by the delete path itself,
    /// and a <see cref="MeshChangeKind.Deleted"/> event carries no authoritative type. The
    /// comparison is case-INSENSITIVE because enrichment resolves NodeType paths that way
    /// (<c>FindStaticNode</c> / the static-provider lookup), so a case-only difference would
    /// recycle a hub onto the identical configuration.</para>
    /// </summary>
    public static bool RequiresRebind(MeshChangeEvent change, string path, string? boundNodeType)
        => change.Kind is MeshChangeKind.Created or MeshChangeKind.Updated
            && string.Equals(change.Path, path, StringComparison.Ordinal)
            && !string.Equals(
                change.NodeType ?? string.Empty,
                boundNodeType ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
}
