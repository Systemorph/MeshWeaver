using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Mesh-level cache of a deck's ordered slides. Every slide of a deck renders
/// the SAME sibling set (prev/next/index/count), so the sibling query + manifest
/// combination is built ONCE per parent path and shared — with
/// <c>Replay(1)</c> semantics — across all of the deck's slide renders. After
/// the deck's first render, every slide switch gets the ordered list
/// synchronously from the replay buffer instead of re-running a live
/// <see cref="IMeshService.Query{T}"/> per render.
/// </summary>
public interface IDeckSlidesCache
{
    /// <summary>
    /// Live observable of the ordered slides under <paramref name="parentPath"/>
    /// (deck-manifest order when the parent is a Deck with a non-empty manifest,
    /// otherwise <see cref="MeshNode.Order"/> — see
    /// <see cref="DeckSlidesCache.OrderSlides"/>). Shared per parent: concurrent
    /// subscribers ride ONE underlying query; a warm entry replays the latest
    /// list synchronously on Subscribe.
    ///
    /// <para>🚨 <b>Read as the VIEWER.</b> The list is the slides the CALLER may read: the
    /// caller's ambient <see cref="AccessContext"/> (request, then circuit) is captured at
    /// THIS call, and the sibling query runs as that viewer whenever it is subscribed. Entries
    /// are shared per (viewer, parent), never across viewers. With no ambient viewer the read
    /// is the anonymous view. A caller that renders off its viewer's delivery (a layout area's
    /// deferred continuation) captures the viewer first (<c>LayoutAreaHost.ViewerContext</c>)
    /// and calls this inside <c>AccessService.SwitchAccessContext(viewer)</c>.</para>
    /// </summary>
    IObservable<IReadOnlyList<MeshNode>> GetOrderedSlides(string parentPath);
}

/// <summary>
/// Default <see cref="IDeckSlidesCache"/>: per-parent
/// <c>Replay(1).RefCount()</c> over the sibling-slide pipeline
/// (query → Scan into a path-keyed map → CombineLatest with the parent node's
/// manifest stream → <see cref="OrderSlides"/>).
/// Registered as a mesh-level singleton in
/// <c>GraphConfigurationExtensions.AddGraph</c> (same lifetime idiom as
/// <see cref="PartitionRegistry"/>); dependencies resolve lazily off the mesh
/// hub so construction never races DI wiring.
///
/// <para><b>Plain <c>RefCount()</c>, deliberately NO disconnect delay.</b> A
/// time-delayed <c>RefCount(TimeSpan)</c> keeps the shared query connected past
/// the last unsubscribe by arming a <see cref="System.Threading.Timer"/> in the
/// PROCESS-GLOBAL TimerQueue — and that timer roots the whole replayed chain,
/// which through the pipeline closure captures the mesh service and hub, so a
/// DISPOSED mesh stays pinned until the delay elapses (repro:
/// <c>MeshHubDisposalLeakTest</c> flagged the TimerQueue→RefCount→DeckSlidesCache
/// chain). Warmth across a slide switch does not need it: the client retires the
/// OUTGOING slide's area stream only once the INCOMING slide's first frame lands
/// (LayoutAreaView's stream hand-over), so the two slides' subscriptions to this
/// shared per-deck entry OVERLAP — the refcount never reaches zero mid-navigation
/// and the Replay(1) buffer is handed to the next slide synchronously. The chain
/// disconnects (and releases the hub) immediately only when the deck is left
/// entirely — no timer, no leak.</para>
/// </summary>
public sealed class DeckSlidesCache : IDeckSlidesCache
{
    private readonly Func<IMeshService> meshService;
    private readonly Func<string, IObservable<MeshNode?>> parentNodes;
    private readonly Func<JsonSerializerOptions> serializerOptions;
    private readonly Func<AccessService?> accessService;
    // 🚨 PromiseCache, not a bare dictionary: Replay(1) is one ReplaySubject behind the
    // connectable and it latches OnError, so a bare dictionary would replay ONE transient query
    // fault to every later viewer of that deck for the life of the process — the deck simply
    // never renders again (#1369). The cache evicts a faulted entry so the next view rebuilds the
    // pipeline; it never re-subscribes on its own.
    //
    // Keyed by (viewer, parent): a shared Replay entry holds ONE viewer's row-level-security view,
    // so an entry shared across viewers would serve one user's readable slides to another
    // (Systemorph/MeshWeaver.Plugins#2802 — the sibling query used to run as System for exactly
    // that reason).
    private readonly PromiseCache<(string Viewer, string Parent), IReadOnlyList<MeshNode>> cache =
        new();

    /// <summary>
    /// DI constructor: binds the cache to the mesh hub. The parent node stream
    /// comes from <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IMessageHub, string)"/>
    /// (the shared <c>IMeshNodeStreamCache</c> handle — one upstream subscription
    /// per path process-wide), NOT a per-slide workspace.
    /// </summary>
    /// <param name="hub">The mesh hub the cache is scoped to.</param>
    public DeckSlidesCache(IMessageHub hub)
        : this(
            () => hub.ServiceProvider.GetRequiredService<IMeshService>(),
            path => hub.GetMeshNodeStream(path).Select(node => (MeshNode?)node),
            () => hub.JsonSerializerOptions,
            () => hub.ServiceProvider.GetService<AccessService>())
    {
    }

    /// <summary>
    /// Seam constructor (unit tests via InternalsVisibleTo): inject the mesh
    /// service, the parent-node stream factory and the serializer options
    /// directly — no hub required.
    /// </summary>
    internal DeckSlidesCache(
        Func<IMeshService> meshService,
        Func<string, IObservable<MeshNode?>> parentNodes,
        Func<JsonSerializerOptions> serializerOptions,
        Func<AccessService?>? accessService = null)
    {
        this.meshService = meshService;
        this.parentNodes = parentNodes;
        this.serializerOptions = serializerOptions;
        this.accessService = accessService ?? (() => null);
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<MeshNode>> GetOrderedSlides(string parentPath)
    {
        var access = accessService();
        var viewer = ViewerOf(access?.Context ?? access?.CircuitContext);
        return cache.GetOrAdd((viewer?.ObjectId ?? "", parentPath), key =>
            BuildOrderedSlides(meshService(), parentNodes(key.Parent), key.Parent, serializerOptions(), access, viewer)
                .Replay(1)
                .RefCount());
    }

    // A viewer is a real principal. A context without an id, a hub credential or a platform
    // principal reads the anonymous view: this cache never answers with System's view of a deck.
    private static AccessContext? ViewerOf(AccessContext? context) =>
        context is { ObjectId: { Length: > 0 } id, IsHub: false }
        && id != WellKnownUsers.System
        && id != WellKnownUsers.Anonymous
            ? context
            : null;

    /// <summary>
    /// The (uncached) sibling-slide pipeline — shared by the cache above and by
    /// the no-cache fallback for minimal fixtures.
    /// Combines the live sibling query (Scan into a path-keyed map so deletions
    /// and updates fold incrementally) with the parent node's Deck manifest and
    /// orders via <see cref="OrderSlides"/>. Deliberately NO <c>StartWith</c> of
    /// an empty slide list: the FIRST emission must already carry the real deck
    /// (an empty-deck first frame renders "Slide 1 / 1" without Prev/Next and
    /// then re-renders — the incomplete-first-frame defect; repro:
    /// <c>SlideLayoutAreaTest.ContentArea_FirstFrame_CarriesDeckPosition</c>).
    /// The manifest stream DOES keep its <c>StartWith(null)</c> — it protects
    /// slides whose parent node doesn't exist (that stream never emits), letting
    /// the combination render on the Order fallback.
    /// </summary>
    internal static IObservable<IReadOnlyList<MeshNode>> BuildOrderedSlides(
        IMeshService meshService,
        IObservable<MeshNode?> parentNode,
        string parentPath,
        JsonSerializerOptions serializerOptions,
        AccessService? accessService,
        AccessContext? viewer)
    {
        // 🚨 The sibling query runs as the VIEWER, never as System. It used to bypass access
        // control on the argument that slide order / prev-next / counter is "navigation chrome".
        // But the list IS data: every sibling's path, name and order, served to a viewer who may
        // read none of them, and the cache shared it across users. Row-level security decides
        // which slides a viewer sees; a slide the viewer cannot read is not in their deck.
        //  - The viewer is CAPTURED by the caller (GetOrderedSlides) and stamped explicitly
        //    (ForViewer), because this stream is consumed in deferred Rx continuations where the
        //    ambient AccessContext does not flow. Resolving it there reads as Anonymous and
        //    answers an EMPTY deck (repro: OrleansSlideNavigationPostgresTest).
        //  - The same viewer is installed around the SUBSCRIBE, which is when the synced query is
        //    issued: opened and closed on the subscribing thread (never Observable.Using, #1790).
        //  - No viewer means explicitly the anonymous view (UserId ""), never the ambient identity
        //    of whatever thread happens to subscribe.
        // 🚨 No `nodeType:` term: the query language's nodeType filter is EQUALITY, and a
        // plugin-installed slide type's identity is its install path (`Publish/Slide`), so a
        // `nodeType:Slide` sibling query silently excluded every plugin-typed slide (education's
        // 116 Publish/Slide nodes rendered "Slide 1 / 1" with no Prev/Next). Query the parent's
        // children and apply the suffix-aware SlideNodeType.Matches in the fold instead — the
        // candidate set is one deck's children, so the wider query costs nothing.
        var request = MeshQueryRequest.FromQuery($"namespace:{parentPath}")
            .ForViewer(viewer?.ObjectId ?? "");

        // Candidate set: the sibling slide nodes (built-in or plugin-typed) sharing this parent.
        var siblingSlides = Observable.Create<QueryResultChange<MeshNode>>(observer =>
            {
                using (viewer is not null ? accessService?.SwitchAccessContext(viewer) : null)
                    return meshService.Query<MeshNode>(request).Subscribe(observer);
            })
            .Scan(ImmutableDictionary<string, MeshNode>.Empty, (map, change) =>
            {
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                    return change.Items
                        .Where(n => SlideNodeType.Matches(n.NodeType))
                        .ToImmutableDictionary(n => n.Path);
                foreach (var item in change.Items)
                    map = change.ChangeType switch
                    {
                        QueryChangeType.Added or QueryChangeType.Updated when SlideNodeType.Matches(item.NodeType)
                            => map.SetItem(item.Path, item),
                        // An update can RETYPE a node away from a slide type — drop it either way.
                        QueryChangeType.Removed or QueryChangeType.Updated => map.Remove(item.Path),
                        _ => map
                    };
                return map;
            });

        // The parent's own node — if it is a Deck with a manifest, that manifest IS the order.
        // StartWith(null) lets the combined stream render on the Order fallback until the parent
        // node arrives (and stays on the fallback for any non-Deck parent, e.g. a Markdown deck).
        var parentManifest = parentNode
            .Select(parent => DeckManifestPaths(parent, parentPath, serializerOptions))
            .StartWith((IReadOnlyList<string>?)null);

        return siblingSlides.CombineLatest(parentManifest, OrderSlides);
    }

    /// <summary>
    /// If <paramref name="parent"/> is a Deck with a non-empty manifest, returns its entries
    /// resolved to full child paths (the deck's declared order); otherwise <c>null</c> (→ the
    /// <see cref="MeshNode.Order"/> fallback).
    /// </summary>
    private static IReadOnlyList<string>? DeckManifestPaths(
        MeshNode? parent, string parentPath, JsonSerializerOptions serializerOptions)
    {
        if (parent is null || !DeckNodeType.Matches(parent.NodeType))
            return null;
        var refs = parent.ContentAs<DeckContent>(serializerOptions)?.Slides;
        if (refs is null || refs.Count == 0)
            return null;
        return refs
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => DeckSelection.ResolveSlidePath(parentPath, r))
            .ToImmutableList();
    }

    /// <summary>
    /// Orders the candidate slide set: by the deck-manifest position when a manifest is present
    /// (slides absent from the manifest fall to the end, then by Order/path); otherwise by
    /// <see cref="MeshNode.Order"/> (null last), ties broken by path.
    /// </summary>
    internal static IReadOnlyList<MeshNode> OrderSlides(
        IReadOnlyDictionary<string, MeshNode> slides, IReadOnlyList<string>? manifestPaths)
    {
        if (manifestPaths is { Count: > 0 })
        {
            var position = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < manifestPaths.Count; i++)
                position.TryAdd(manifestPaths[i], i);
            return slides.Values
                .OrderBy(n => position.TryGetValue(n.Path, out var i) ? i : int.MaxValue)
                .ThenBy(n => n.Order ?? int.MaxValue)
                .ThenBy(n => n.Path, StringComparer.Ordinal)
                .ToImmutableList();
        }

        return slides.Values
            .OrderBy(n => n.Order ?? int.MaxValue)
            .ThenBy(n => n.Path, StringComparer.Ordinal)
            .ToImmutableList();
    }
}
