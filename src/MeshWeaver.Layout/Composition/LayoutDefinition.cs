using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Composition;

/// <summary>
/// Synchronous renderer delegate: receives the layout-area host, the rendering context, and
/// the current entity store, and returns an updated <see cref="EntityStoreAndUpdates"/> in a
/// single call. Used with <see cref="LayoutDefinition.WithRenderer(Func{RenderingContext,bool},Renderer)"/>
/// when the output is fully known at call time (no live updates needed).
/// </summary>
public delegate EntityStoreAndUpdates Renderer(LayoutAreaHost host, RenderingContext context, EntityStore store);

/// <summary>
/// The reactive renderer the layout engine composes for an area. Returns an
/// <see cref="IObservable{T}"/> of <see cref="EntityStoreAndUpdates"/> — the area's
/// content as it arrives: a renderer for a synchronous control emits once
/// (<c>Observable.Return</c>), a renderer for a live generator re-emits over the
/// area's lifetime. Every emission flows through the layout-area init subscription
/// as a <c>SetCurrent</c>, so a synchronous emission produced during init is never
/// dropped by the init window (the bug the old async <c>RenderAsync</c> hit).
/// </summary>
public delegate IObservable<EntityStoreAndUpdates> ObservableRenderer(LayoutAreaHost host, RenderingContext context, EntityStore store);

/// <summary>
/// Immutable configuration record for a hub's layout engine. Collects named and predicate
/// renderers registered at startup, object-to-control conversion rules, and area definitions.
/// Built fluently via <c>WithRenderer</c>, <c>WithNamedRenderer</c>, <c>AddRendering</c>, etc.
/// </summary>
/// <param name="Hub">The message hub that owns this layout definition.</param>
public record LayoutDefinition(IMessageHub Hub)
{
    private ImmutableList<(Func<RenderingContext, bool> Filter, ObservableRenderer Renderer)> Renderers { get; init; } = [];

    private ImmutableDictionary<string, ObservableRenderer> NamedRenderers { get; init; }
        = ImmutableDictionary<string, ObservableRenderer>.Empty;

    /// <summary>
    /// What each node LANDING PAGE registered on this definition does about the provenance line —
    /// see <see cref="NodePageProvenance"/>. Recorded by
    /// <c>MeshNodeLayoutAreas.WithNodePage</c>; read back with
    /// <see cref="GetNodePageProvenance"/>.
    ///
    /// <para>🚨 An entry is REMOVED by <see cref="WithNamedRenderer(string, ObservableRenderer)"/>,
    /// which every <c>WithView(area, …)</c> overload funnels through. That is the invariant that
    /// keeps the record honest: a verdict describes ONE renderer, so replacing the renderer — which
    /// is exactly how a node type takes over a landing page — discards it. Without the removal the
    /// framework's own "the page renders it" verdict for <c>Overview</c> would survive every
    /// override and every check would pass having measured a renderer that is no longer there.</para>
    /// </summary>
    private ImmutableDictionary<string, NodePageProvenance> NodePages { get; init; }
        = ImmutableDictionary<string, NodePageProvenance>.Empty;

    /// <summary>
    /// The default area to display when no area is specified in the URL.
    /// </summary>
    public string? DefaultArea { get; init; }

    /// <summary>
    /// Optional STATUS-GATED EMERGENCY-MODE RENDERING gate (see <see cref="RenderingGate"/>).
    /// When configured, the <see cref="LayoutAreaHost"/> invokes the registered renderers — the
    /// typed-content readers — ONLY while the gate reports a SUCCESS status; an error/cancelled
    /// status or missing configuration short-circuits every area of this hub to a visible
    /// emergency error frame instead. Null (the default) renders unconditionally.
    /// </summary>
    public RenderingGate? RenderingGate { get; init; }

    /// <summary>
    /// Configures status-gated emergency-mode rendering for every area of this hub: renderers
    /// (the typed-content readers) run only on a SUCCESS status; ERROR/CANCELLED status and
    /// missing configuration render a visible emergency error frame — never a hang, never an
    /// empty render. See <see cref="RenderingGate"/> for the full law.
    /// </summary>
    /// <param name="gate">The status source evaluated per layout-area host.</param>
    public LayoutDefinition WithRenderingGate(RenderingGate gate)
        => this with { RenderingGate = gate };

    /// <summary>
    /// Sets the default area to display when no area is specified in the URL.
    /// </summary>
    public LayoutDefinition WithDefaultArea(string area)
        => this with { DefaultArea = area };

    /// <summary>
    /// Registers a synchronous predicate renderer. Wraps <paramref name="renderer"/> in
    /// <c>Observable.Return</c> so it emits exactly once per request.
    /// </summary>
    /// <param name="filter">Predicate that determines whether this renderer applies to a given context.</param>
    /// <param name="renderer">The synchronous renderer to register.</param>
    /// <returns>A new <see cref="LayoutDefinition"/> with the renderer appended.</returns>
    public LayoutDefinition WithRenderer(Func<RenderingContext, bool> filter, Renderer renderer)
        => WithRenderer(filter, (h, ctx, s) => Observable.Return(renderer.Invoke(h, ctx, s)));

    /// <summary>
    /// Registers a reactive predicate renderer that can emit multiple times over the area's lifetime.
    /// </summary>
    /// <param name="filter">Predicate that determines whether this renderer applies to a given context.</param>
    /// <param name="renderer">The observable renderer to register.</param>
    /// <returns>A new <see cref="LayoutDefinition"/> with the renderer appended.</returns>
    public LayoutDefinition WithRenderer(Func<RenderingContext, bool> filter, ObservableRenderer renderer)
        => this with
        {
            Renderers = Renderers.Add((filter, renderer))
        };

    /// <summary>
    /// Registers a synchronous renderer for the named area <paramref name="area"/>.
    /// </summary>
    /// <param name="area">The area name (matched against <c>RenderingContext.Area</c>).</param>
    /// <param name="renderer">The synchronous renderer to invoke for this area.</param>
    /// <returns>A new <see cref="LayoutDefinition"/> with the named renderer registered.</returns>
    public LayoutDefinition WithNamedRenderer(string area, Renderer renderer)
        => WithNamedRenderer(area, (h, ctx, s) => Observable.Return(renderer.Invoke(h, ctx, s)));

    /// <summary>
    /// Registers a reactive renderer for the named area <paramref name="area"/>, replacing any
    /// previously registered renderer for that name.
    /// </summary>
    /// <param name="area">The area name (matched against <c>RenderingContext.Area</c>).</param>
    /// <param name="renderer">The observable renderer to invoke for this area.</param>
    /// <returns>A new <see cref="LayoutDefinition"/> with the named renderer registered.</returns>
    public LayoutDefinition WithNamedRenderer(string area, ObservableRenderer renderer)
        => this with
        {
            NamedRenderers = NamedRenderers.SetItem(area, renderer),
            // 🚨 The provenance verdict describes the renderer being replaced here, so it goes with
            // it — see NodePages. Taking over a landing page and inheriting the previous page's
            // verdict is the one way this record could lie (#4500). Measured negative control:
            // remove this line and NodePageProvenanceVerdictTest
            // .A_verdict_does_not_survive_the_renderer_it_describes reads RenderedByThePage where
            // null is required — i.e. the takeover inherits the page it replaced.
            NodePages = NodePages.Remove(area)
        };

    /// <summary>
    /// Records what the landing page registered for <paramref name="area"/> does about the
    /// provenance line. Called by <c>MeshNodeLayoutAreas.WithNodePage</c> AFTER the renderer is
    /// registered — the order matters, because registering clears the entry.
    /// </summary>
    /// <param name="area">The area whose renderer the verdict describes.</param>
    /// <param name="provenance">The verdict — see <see cref="NodePageProvenance"/>.</param>
    public LayoutDefinition WithNodePageProvenance(string area, NodePageProvenance provenance)
        => this with { NodePages = NodePages.SetItem(area, provenance) };

    /// <summary>
    /// The recorded verdict for <paramref name="area"/>, or <c>null</c> when the area's renderer
    /// never declared one.
    ///
    /// <para>🚨 <c>null</c> on this hub's <see cref="DefaultArea"/> is the defect #4500 is about:
    /// the page people land on replaced the framework's renderer and nobody said what should
    /// happen to the provenance line. It is NOT the same as
    /// <see cref="NodePageProvenanceKind.Declined"/> — a decline is an answer.</para>
    /// </summary>
    /// <param name="area">The area to ask about.</param>
    public NodePageProvenance? GetNodePageProvenance(string area)
        => NodePages.GetValueOrDefault(area);

    /// <summary>
    /// True when a named renderer is registered for <paramref name="area"/>. Predicate
    /// (catch-all) views use this to keep OFF areas that are explicitly owned: every
    /// renderer matching an area first disposes and REMOVES that area's existing content
    /// (<c>RenderObservable</c> → <c>DisposeExistingAreas</c>), so two renderers for the
    /// same area are last-wins-destructive. A <c>WithView(_ =&gt; true, …)</c> that also
    /// matched named areas silently wiped them — the kernel catch-all did exactly this on
    /// every Activity hub, and NO named area (Progress/Overview/Thumbnail) ever rendered
    /// there (the 2026-07-03 eternal-spinner RCA).
    /// </summary>
    /// <param name="area">The area name to test.</param>
    public bool HasNamedRenderer(string area) => NamedRenderers.ContainsKey(area);

    /// <summary>
    /// Renders <paramref name="context"/>'s area reactively. Composes the named
    /// renderer (if one is registered for <c>context.Area</c>) and every matching
    /// predicate renderer, accumulating their <see cref="EntityStoreAndUpdates"/> onto
    /// the running store, and emits each accumulated store over time as the area's
    /// content arrives. When no renderer matches at all, emits a visible
    /// "area not found" <see cref="MarkdownControl"/> so the client never spins forever
    /// waiting for content no renderer will produce.
    /// </summary>
    public IObservable<EntityStoreAndUpdates> Render(
        LayoutAreaHost host,
        RenderingContext context,
        EntityStore store)
    {
        // Collect the renderers that apply to this area, in order: the named renderer
        // first, then every predicate renderer whose filter matches.
        var rendererList = ImmutableList.CreateBuilder<ObservableRenderer>();
        if (NamedRenderers.TryGetValue(context.Area, out var namedRenderer))
            rendererList.Add(namedRenderer);
        foreach (var (filter, renderer) in Renderers)
            if (filter(context))
                rendererList.Add(renderer);
        var applicable = rendererList.ToImmutable();

        if (applicable.Count == 0)
            // No renderer for this area at all — surface a visible placeholder.
            return Observable.Return(NotFound(host, context, store));

        // Compose the applicable renderers. ONE in the common case (a single named area, or a single
        // predicate match); SEQUENTIALLY when several apply (a named area PLUS the predicate menu, or
        // two WithNavMenu registrations) — each renders onto the store the PREVIOUS renderer produced,
        // not against the bare base — because a renderer can read what an earlier one wrote (two
        // WithNavMenu calls accumulate their links onto the same NavMenuControl; the second reads the
        // first's control from the store). SelectMany re-runs the downstream when any upstream re-emits
        // (a live renderer — e.g. the permission-reactive menu — re-emitting re-threads the chain).
        IObservable<EntityStoreAndUpdates> composed;
        if (applicable.Count == 1)
            composed = applicable[0].Invoke(host, context, store);
        else
        {
            var seed = Observable.Return(new EntityStoreAndUpdates(store, [], host.Stream.StreamId));
            composed = applicable.Aggregate(seed, (accObservable, renderer) =>
                accObservable.SelectMany(acc =>
                    renderer.Invoke(host, context, acc.Store)
                        .Select(slice => new EntityStoreAndUpdates(
                            slice.Store,
                            acc.Updates.Concat(slice.Updates),
                            host.Stream.StreamId))));
        }

        // 🚨 A control for the REQUESTED area can STILL be absent after every applicable renderer ran.
        // The node menu is registered as a GLOBAL predicate renderer (`WithRenderer(_ => true, …)` in
        // NodeMenuItemsExtensions), so it matches EVERY area — `applicable.Count` is >= 1 even for an
        // area that NOTHING actually renders. The Count==0 branch above therefore never fires on a real
        // node hub, and an unknown area would render only the menu, leave /areas/{area} empty, and the
        // client's control stream for {area} would never emit → the view spins on "Building layout…"
        // forever → resubscribe/re-render storm → circuit wedge (the 2026-06-28 home/side-panel wedge,
        // pinned by OrleansUnresolvableAreaNoWedgeTest).
        //
        // Surface the NotFound placeholder as a TERMINAL FALLBACK — never per-frame: pass every produced
        // frame through unchanged, and inject the placeholder ONLY when the render stream COMPLETES
        // without ever having produced the requested area. Completion is the structural signal that "no
        // more content is coming": a genuinely-unrendered area is rendered by the synchronous menu alone
        // (`Observable.Return`, which completes) → placeholder, fast. A LIVE renderer that never completes
        // (e.g. a kernel cell whose `/areas/{id}` is filled only once a script has actually run — the
        // area stream is hot) keeps the area legitimately PENDING; its real content flows through when it
        // arrives. Appending the placeholder per-frame instead clobbered every async area with a transient
        // "**Area not found**" on its first empty frame — the regression that made every kernel cell emit
        // the placeholder before its result (MonolithKernelTest / InteractiveMarkdownExecutionTest).
        if (string.IsNullOrEmpty(context.Area))
            return composed;

        return Observable.Create<EntityStoreAndUpdates>(observer =>
        {
            var areaProduced = false;
            EntityStoreAndUpdates? last = null;
            return composed.Subscribe(
                result =>
                {
                    last = result;
                    if (StoreHasArea(result.Store, context.Area))
                        areaProduced = true;
                    observer.OnNext(result);
                },
                observer.OnError,
                () =>
                {
                    if (!areaProduced)
                        observer.OnNext(last is null
                            ? NotFound(host, context, store)
                            : AppendNotFound(last, host, context));
                    observer.OnCompleted();
                });
        });
    }

    /// <summary>True when the produced store carries a control at <c>/areas/{area}</c>.</summary>
    private static bool StoreHasArea(EntityStore store, string area) =>
        store.Collections.GetValueOrDefault(LayoutAreaReference.Areas)?.Instances.ContainsKey(area) == true;

    /// <summary>
    /// The visible "Area not found" placeholder control — shown instead of an eternal spinner when no
    /// renderer produced content for the requested area. Lists the hub's named areas to aid diagnosis.
    ///
    /// <para>🚨 Carries <see cref="AreaFrameClassifier.AreaNotFoundId"/> as its
    /// <see cref="UiControl.Id"/> so a consumer can tell this VERDICT ("nothing will ever render
    /// here") apart from the compile-progress PROMISE an instance serves while its NodeType is
    /// still building — the two are indistinguishable to anything that only reads the prose.
    /// Keep the id stable; the markdown is free to change.</para>
    /// </summary>
    private MarkdownControl BuildNotFoundControl(LayoutAreaHost host, object area)
    {
        var availableAreas = NamedRenderers.Keys.OrderBy(k => k).ToArray();
        var availableLine = availableAreas.Length == 0
            ? "_no named areas registered on this hub_"
            : "Available named areas: " + string.Join(", ", availableAreas.Select(a => $"`{a}`"));
        return new MarkdownControl(
            $"**Area not found**\n\nNo renderer is registered for area `{area}` on hub `{host.Hub.Address}`.\n\n{availableLine}")
        {
            Id = AreaFrameClassifier.AreaNotFoundId
        };
    }

    private EntityStoreAndUpdates NotFound(LayoutAreaHost host, RenderingContext context, EntityStore store)
    {
        var notFound = BuildNotFoundControl(host, context.Area);
        return new EntityStoreAndUpdates(
            store.Update(LayoutAreaReference.Areas, coll => coll.SetItem(context.Area, notFound)),
            [new EntityUpdate(LayoutAreaReference.Areas, context.Area, notFound)],
            host.Stream.StreamId);
    }

    /// <summary>
    /// Appends the NotFound placeholder for the requested area ONTO an already-rendered result
    /// (preserving the menu and any other sidecar content), for the case where applicable renderers
    /// ran but none produced the requested area's control.
    /// </summary>
    private EntityStoreAndUpdates AppendNotFound(EntityStoreAndUpdates result, LayoutAreaHost host, RenderingContext context)
    {
        var notFound = BuildNotFoundControl(host, context.Area);
        return new EntityStoreAndUpdates(
            result.Store.Update(LayoutAreaReference.Areas, coll => coll.SetItem(context.Area, notFound)),
            result.Updates.Append(new EntityUpdate(LayoutAreaReference.Areas, context.Area, notFound)),
            host.Stream.StreamId);
    }

    /// <summary>
    /// Total number of renderers registered on this definition (predicate + named).
    /// </summary>
    public int Count => Renderers.Count + NamedRenderers.Count;

    /// <summary>
    /// Conversion rules collected at config time. Applied by UiControlService at construction.
    /// </summary>
    internal ImmutableList<Func<object, UiControl?>> ConversionRules { get; init; } = ImmutableList<Func<object, UiControl?>>.Empty;

    /// <summary>
    /// Prepends a conversion rule that maps an arbitrary object to a <see cref="UiControl"/>.
    /// Rules are tried in registration order; the first non-null result wins.
    /// </summary>
    /// <param name="rule">A function that returns a <see cref="UiControl"/> or <c>null</c> when not applicable.</param>
    /// <returns>A new <see cref="LayoutDefinition"/> with <paramref name="rule"/> prepended.</returns>
    public LayoutDefinition AddRendering(Func<object, UiControl?> rule)
        => this with { ConversionRules = ConversionRules.Insert(0, rule) };

    internal ImmutableDictionary<string, LayoutAreaDefinition> AreaDefinitions { get; init; } = ImmutableDictionary<string, LayoutAreaDefinition>.Empty;
    internal ThumbnailPattern? ThumbnailPattern { get; init; }

    /// <summary>
    /// Configures thumbnails using the default naming convention: {basePath}/{area}.png and {basePath}/{area}-dark.png
    /// </summary>
    public LayoutDefinition WithThumbnailsPath(string basePath)
        => this with { ThumbnailPattern = ThumbnailPattern.FromBasePath(basePath) };

    /// <summary>
    /// Configures thumbnails using lambda expressions to generate URLs from area names.
    /// </summary>
    public LayoutDefinition WithThumbnailPattern(Func<string, string> lightUrlFactory, Func<string, string> darkUrlFactory)
        => this with { ThumbnailPattern = new ThumbnailPattern(lightUrlFactory, darkUrlFactory) };

    /// <summary>
    /// Configures thumbnails using a custom pattern.
    /// </summary>
    public LayoutDefinition WithThumbnailPattern(ThumbnailPattern pattern)
        => this with { ThumbnailPattern = pattern };

    /// <summary>
    /// Registers a <see cref="LayoutAreaDefinition"/> keyed by its area name. When
    /// <paramref name="layoutArea"/> is <c>null</c> the definition is returned unchanged.
    /// </summary>
    /// <param name="layoutArea">The area definition to register, or <c>null</c> to no-op.</param>
    /// <returns>A new <see cref="LayoutDefinition"/> with the area definition stored.</returns>
    public LayoutDefinition WithAreaDefinition(LayoutAreaDefinition? layoutArea) => 
        layoutArea == null 
            ? this 
            : this with { AreaDefinitions = AreaDefinitions.SetItem(layoutArea.Area, layoutArea) };
}


