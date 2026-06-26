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
    /// The default area to display when no area is specified in the URL.
    /// </summary>
    public string? DefaultArea { get; init; }

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
            NamedRenderers = NamedRenderers.SetItem(area, renderer)
        };

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
            // No renderer for this area — surface a visible placeholder.
            return Observable.Return(NotFound(host, context, store));

        // Single renderer (the overwhelmingly common case: one named area, or one
        // predicate match) — render directly onto the base store. Each emission is the
        // renderer's own EntityStoreAndUpdates, which the init subscription sets as
        // Current. A synchronous control emits once; a live generator re-emits.
        if (applicable.Count == 1)
            return applicable[0].Invoke(host, context, store);

        // Multiple renderers (e.g. two WithNavMenu registrations for the same area, or a
        // named area PLUS the predicate menu renderer). They must compose SEQUENTIALLY —
        // each renders onto the store the PREVIOUS renderer produced, not against the
        // bare base — because a renderer can read what an earlier one wrote (two
        // WithNavMenu calls accumulate their links onto the same NavMenuControl; the
        // second reads the first's control from the store). We fold the renderers into a
        // chain where renderer N's input store is renderer N-1's latest output, and
        // SelectMany re-runs the downstream when any upstream re-emits (a live renderer —
        // e.g. the permission-reactive menu — re-emitting re-threads the chain). The
        // running Updates are accumulated alongside the store.
        var seed = Observable.Return(new EntityStoreAndUpdates(store, [], host.Stream.StreamId));
        return applicable.Aggregate(seed, (accObservable, renderer) =>
            accObservable.SelectMany(acc =>
                renderer.Invoke(host, context, acc.Store)
                    .Select(slice => new EntityStoreAndUpdates(
                        slice.Store,
                        acc.Updates.Concat(slice.Updates),
                        host.Stream.StreamId))));
    }

    private EntityStoreAndUpdates NotFound(LayoutAreaHost host, RenderingContext context, EntityStore store)
    {
        // Surface a visible "area not found" placeholder so the client doesn't spin forever
        // waiting for an Update that no renderer is going to produce.
        var availableAreas = NamedRenderers.Keys.OrderBy(k => k).ToArray();
        var availableLine = availableAreas.Length == 0
            ? "_no named areas registered on this hub_"
            : "Available named areas: " + string.Join(", ", availableAreas.Select(a => $"`{a}`"));
        var notFound = new MarkdownControl(
            $"**Area not found**\n\nNo renderer is registered for area `{context.Area}` on hub `{host.Hub.Address}`.\n\n{availableLine}");
        return new EntityStoreAndUpdates(
            store.Update(LayoutAreaReference.Areas, coll => coll.SetItem(context.Area, notFound)),
            [new EntityUpdate(LayoutAreaReference.Areas, context.Area, notFound)],
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


