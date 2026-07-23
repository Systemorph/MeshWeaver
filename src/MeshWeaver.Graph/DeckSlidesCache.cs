using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
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
    /// </summary>
    IObservable<IReadOnlyList<MeshNode>> GetOrderedSlides(string parentPath);
}

/// <summary>
/// Default <see cref="IDeckSlidesCache"/>: per-parent
/// <c>Replay(1).RefCount(disconnectDelay)</c> over the sibling-slide pipeline
/// (query → Scan into a path-keyed map → CombineLatest with the parent node's
/// manifest stream → <see cref="OrderSlides"/>). The disconnect delay keeps the
/// live query connected across the subscriber-free gaps between slide
/// navigations, so the next slide's render is a warm synchronous hit.
/// Registered as a mesh-level singleton in
/// <c>GraphConfigurationExtensions.AddGraph</c> (same lifetime idiom as
/// <see cref="PartitionRegistry"/>); dependencies resolve lazily off the mesh
/// hub so construction never races DI wiring.
/// </summary>
public sealed class DeckSlidesCache : IDeckSlidesCache
{
    /// <summary>
    /// How long a parent's replayed pipeline stays CONNECTED after its last
    /// subscriber disconnects. Slide renders subscribe only transiently, so a
    /// plain <c>RefCount()</c> would tear the shared query down between two
    /// navigations and cold-start every slide switch; a few minutes comfortably
    /// covers a presentation's between-slide gaps.
    /// </summary>
    private static readonly TimeSpan DefaultDisconnectDelay = TimeSpan.FromMinutes(3);

    private readonly Func<IMeshService> meshService;
    private readonly Func<string, IObservable<MeshNode?>> parentNodes;
    private readonly Func<JsonSerializerOptions> serializerOptions;
    private readonly Func<AccessService?> accessService;
    private readonly TimeSpan disconnectDelay;
    private readonly ConcurrentDictionary<string, IObservable<IReadOnlyList<MeshNode>>> cache =
        new(StringComparer.Ordinal);

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
            DefaultDisconnectDelay,
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
        TimeSpan disconnectDelay,
        Func<AccessService?>? accessService = null)
    {
        this.meshService = meshService;
        this.parentNodes = parentNodes;
        this.serializerOptions = serializerOptions;
        this.disconnectDelay = disconnectDelay;
        this.accessService = accessService ?? (() => null);
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<MeshNode>> GetOrderedSlides(string parentPath) =>
        cache.GetOrAdd(parentPath, path =>
            BuildOrderedSlides(meshService(), parentNodes(path), path, serializerOptions(), accessService())
                .Replay(1)
                .RefCount(disconnectDelay));

    /// <summary>
    /// The (uncached) sibling-slide pipeline — shared by the cache above and by
    /// <see cref="SlideLayoutAreas"/>' no-cache fallback for minimal fixtures.
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
        AccessService? accessService)
    {
        // 🚨 The sibling query MUST bypass access control — same sanctioned pattern
        // (and justification) as PathResolutionService's routing query. The deck's
        // slide order / prev-next / counter is NAVIGATION CHROME, not data access:
        // the actual slide CONTENT is access-checked by the target hub when the
        // user navigates there. Two reasons System is required, not just nice:
        //  1. The layout host consumes this stream in DEFERRED Rx continuations
        //     where the per-circuit AsyncLocal AccessContext does not flow — under
        //     the PG per-schema access clause the query then runs as Anonymous and
        //     returns an EMPTY deck forever (counter stuck at "Slide 1 / 1", no
        //     Prev/Next; repro: OrleansSlideNavigationPostgresTest).
        //  2. The cache above is shared ACROSS users — a shared Replay entry must
        //     hold user-INDEPENDENT results, or one user's RLS view would be
        //     served to another. Bypass requires BOTH UserId=System on the request
        //     AND the ImpersonateAsSystem AsyncLocal scope (defense-in-depth, see
        //     PathResolutionService).
        var request = MeshQueryRequest.FromQuery(
            $"namespace:{parentPath} nodeType:{SlideNodeType.NodeType}") with
        {
            UserId = MeshWeaver.Mesh.Security.WellKnownUsers.System,
        };

        // Candidate set: the sibling Slide nodes sharing this parent.
        var siblingSlides = Observable.Using(
                () => accessService?.ImpersonateAsSystem()
                      ?? System.Reactive.Disposables.Disposable.Empty,
                _ => meshService.Query<MeshNode>(request))
            .Scan(ImmutableDictionary<string, MeshNode>.Empty, (map, change) =>
            {
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                    return change.Items.ToImmutableDictionary(n => n.Path);
                foreach (var item in change.Items)
                    map = change.ChangeType switch
                    {
                        QueryChangeType.Added or QueryChangeType.Updated => map.SetItem(item.Path, item),
                        QueryChangeType.Removed => map.Remove(item.Path),
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
        if (parent is null || !string.Equals(parent.NodeType, DeckNodeType.NodeType, StringComparison.Ordinal))
            return null;
        var refs = parent.ContentAs<DeckContent>(serializerOptions)?.Slides;
        if (refs is null || refs.Count == 0)
            return null;
        return refs
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => DeckLayoutAreas.ResolveSlidePath(parentPath, r))
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
