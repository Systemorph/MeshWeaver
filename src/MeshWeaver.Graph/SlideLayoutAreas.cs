using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for Slide nodes — presentation-style pages for training material.
/// <list type="bullet">
///   <item><b>Content</b> (default): the slide rendered on a 16:9 stage with a slim
///     presenter bar underneath (Prev / "Slide n / N" / Deck / Present / Next).
///     Clicking the stage advances to the next slide.</item>
///   <item><b>Present</b>: the chrome-free stage — click to advance, a small corner
///     counter is the only overlay. Open this full-screen to present.</item>
///   <item><b>Notes</b>: speaker notes plus a compact preview of the slide.</item>
/// </list>
/// When a slide's parent is a <see cref="DeckNodeType">Deck</see> with a non-empty
/// <see cref="DeckContent.Slides"/> manifest, prev/next/index/count follow that EXTERNAL,
/// deck-owned order. Otherwise a deck is any parent whose children are Slide nodes and
/// prev/next resolve the sibling Slide nodes of the same parent ordered by
/// <see cref="MeshNode.Order"/> (lower first, null last, ties broken by path) — so existing
/// decks with Markdown/Space parents keep working unchanged. Navigation uses the standard
/// href/redirect mechanism (<c>WithNavigateToHref</c> for buttons, <see cref="RedirectControl"/>
/// from the stage click action) — no bespoke messages.
/// </summary>
public static class SlideLayoutAreas
{
    /// <summary>Area name for the Content layout area (default).</summary>
    public const string ContentArea = "Content";
    /// <summary>Area name for the chrome-free Present layout area.</summary>
    public const string PresentArea = "Present";
    /// <summary>Area name for the speaker Notes layout area.</summary>
    public const string NotesArea = "Notes";

    /// <summary>Area id of the slide stage inside the Content / Present / Notes areas.</summary>
    public const string StageArea = "Stage";
    /// <summary>Area id of the slide body (markdown canvas) inside the stage.</summary>
    public const string SlideBodyArea = "Body";
    /// <summary>Area id of the invisible presenter-keyboard driver in the Present area.</summary>
    public const string SlideShowArea = "SlideShow";
    /// <summary>Area id of the presenter bar beneath the stage in the Content area.</summary>
    public const string PresenterBarArea = "PresenterBar";
    /// <summary>Area id of the "Slide n / N" counter.</summary>
    public const string CounterArea = "Counter";
    /// <summary>Area id of the Prev button inside the presenter bar.</summary>
    public const string PrevButtonArea = "Prev";
    /// <summary>Area id of the Next button inside the presenter bar.</summary>
    public const string NextButtonArea = "Next";
    /// <summary>Area id of the deck (parent) link inside the presenter bar.</summary>
    public const string DeckLinkArea = "Deck";
    /// <summary>Area id of the "Present" link inside the presenter bar.</summary>
    public const string PresentLinkArea = "PresentLink";
    /// <summary>Area id of the notes body inside the Notes area.</summary>
    public const string NotesBodyArea = "NotesBody";

    /// <summary>
    /// The presentation theme ("Deep Indigo") exposed as CSS custom properties on the stage /
    /// present-root, so every slide (and its author-supplied HTML) references the SAME token set —
    /// one source of truth, theme-INDEPENDENT (it does not flip with the portal light/dark theme).
    /// A slide that sets its own <see cref="SlideContent.Background"/> / element colors still wins;
    /// these are the defaults so a slide with no colors already looks on-theme (and never renders
    /// white-on-white or dark-blue-on-black).
    /// <list type="bullet">
    ///   <item><c>--ae-bg</c> — the slide backdrop gradient · <c>--ae-bg-solid</c> — the letterbox fill</item>
    ///   <item><c>--ae-fg</c> — primary text · <c>--ae-muted</c> — eyebrows/captions</item>
    ///   <item><c>--ae-accent</c> — key words / links · <c>--ae-accent2</c> — secondary / illustration</item>
    /// </list>
    /// </summary>
    internal const string ThemeTokens =
        "--ae-bg: linear-gradient(135deg, #0b1d3a 0%, #3b1d6e 100%);" +
        "--ae-bg-solid: #0b1d3a;" +
        "--ae-fg: #f4f7ff;" +
        "--ae-muted: #9db8ff;" +
        "--ae-accent: #3b82f6;" +
        "--ae-accent2: #818cf8;";

    /// <summary>
    /// The default stage background when a slide sets no <see cref="SlideContent.Background"/> — the
    /// theme-independent Deep-Indigo backdrop (<c>var(--ae-bg)</c>), so default slides are readable
    /// in any portal theme. A slide's own <see cref="SlideContent.Background"/> overrides it.
    /// </summary>
    private const string DefaultBackground = "var(--ae-bg)";

    private const string EmptySlideHint =
        "*This slide has no content yet. Edit the node's `Content` to fill the stage.*";

    /// <summary>
    /// Registers the Slide node views (Content, Present, Notes and the standard
    /// create/delete areas) on the hub configuration.
    /// </summary>
    /// <param name="configuration">The message hub configuration to register on.</param>
    /// <returns>The configuration with the Slide views registered.</returns>
    public static MessageHubConfiguration AddSlideViews(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            // Slides want a wide reading column — the stage is the page.
            .Set(new PageLayoutOptions { MaxWidth = "1400px" })
            .AddLayout(layout => layout
                .WithDefaultArea(ContentArea)
                .WithView(ContentArea, Content)
                .WithView(PresentArea, Present)
                .WithView(NotesArea, Notes)
                .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateLayoutArea.Create)
                .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>
    /// Renders the default Content area: the slide stage (click advances to the next
    /// slide) with a slim presenter bar underneath — Prev / "Slide n / N" / Deck /
    /// Present / Next. Prev/Next resolve sibling Slide nodes of the same parent.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the view for the Content layout area.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .CombineLatest(ObserveDeckSlides(host),
                (node, slides) => (UiControl?)BuildContent(host, node, slides));

    /// <summary>
    /// Renders the Present area: just the stage, click-to-advance (staying in Present
    /// mode), with a minimal counter overlay in the corner. This is what a presenter
    /// opens full-screen in the browser.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the view for the Present layout area.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Present(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .CombineLatest(ObserveDeckSlides(host),
                (node, slides) => (UiControl?)BuildPresent(host, node, slides));

    /// <summary>
    /// Renders the Notes area: the speaker notes (markdown) with a compact preview of
    /// the slide below.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the view for the Notes layout area.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Notes(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .Select(node => (UiControl?)BuildNotes(host, node));

    // ── Content ──────────────────────────────────────────────────────────────

    private static UiControl BuildContent(LayoutAreaHost host, MeshNode? node, IReadOnlyList<MeshNode> slides)
    {
        var hubPath = host.Hub.Address.ToString();
        var slide = node.ContentAs<SlideContent>(host.Hub.JsonSerializerOptions);
        var (index, prev, next) = Locate(hubPath, slides);
        var nextHref = next is null ? null : MeshNodeLayoutAreas.BuildUrl(next.Path, ContentArea);

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        container = container.WithView(BuildStage(slide, nextHref), StageArea);
        container = container.WithView(
            BuildPresenterBar(hubPath, index, slides.Count, prev, next), PresenterBarArea);

        return container;
    }

    /// <summary>
    /// The slim presenter bar under the stage: ◀ Prev / "Slide n / N" / Deck /
    /// Present / Next ▶. Prev/Next render only when a sibling exists in that
    /// direction; all navigation goes through the standard href mechanism.
    /// </summary>
    private static UiControl BuildPresenterBar(
        string hubPath, int index, int count, MeshNode? prev, MeshNode? next)
    {
        var bar = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; align-items: center; gap: 10px; margin-top: 14px;");

        if (prev is not null)
            bar = bar.WithView(Controls.Button("◀ Prev")
                    .WithAppearance(Appearance.Neutral)
                    .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(prev.Path, ContentArea)),
                PrevButtonArea);

        bar = bar.WithView(BuildCounter(index, count)
                .WithStyle("font-variant-numeric: tabular-nums; color: var(--neutral-foreground-hint);"),
            CounterArea);

        var parentPath = GetParentPath(hubPath);
        if (parentPath is not null)
            bar = bar.WithView(Controls.Button("Deck")
                    .WithAppearance(Appearance.Lightweight)
                    .WithNavigateToHref($"/{parentPath}"),
                DeckLinkArea);

        bar = bar.WithView(Controls.Button("Present")
                .WithAppearance(Appearance.Lightweight)
                .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(hubPath, PresentArea)),
            PresentLinkArea);

        if (next is not null)
            bar = bar.WithView(Controls.Button("Next ▶")
                    .WithAppearance(Appearance.Accent)
                    .WithStyle("margin-left: auto;")
                    .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(next.Path, ContentArea)),
                NextButtonArea);

        return bar;
    }

    // ── Present ──────────────────────────────────────────────────────────────

    private static UiControl BuildPresent(LayoutAreaHost host, MeshNode? node, IReadOnlyList<MeshNode> slides)
    {
        var hubPath = host.Hub.Address.ToString();
        var slide = node.ContentAs<SlideContent>(host.Hub.JsonSerializerOptions);
        var (index, prev, next) = Locate(hubPath, slides);
        // Advancing in Present mode stays in Present mode.
        var nextHref = next is null ? null : MeshNodeLayoutAreas.BuildUrl(next.Path, PresentArea);

        var root = BuildPresentRoot();
        root = root.WithView(BuildStage(slide, nextHref, present: true), StageArea);

        // Minimal overlay counter, bottom-right corner chip — readable on the dark backdrop.
        root = root.WithView(BuildPresentCounter(index, slides.Count), CounterArea);

        // Keyboard driver (backward-compat standalone slide walk): prev/next resolve the
        // ordered sibling / deck-manifest slides; Esc exits to the deck (parent) overview.
        root = root.WithView(BuildSlideShowForSlide(hubPath, prev, next, slides), SlideShowArea);

        return root;
    }

    /// <summary>
    /// The full-bleed presentation root: fills the viewport (the portal hides its chrome on a
    /// <c>/Present</c> route), centers the 16:9 stage with minimal padding, and fills the letterbox
    /// with the theme's solid backdrop. Carries the <see cref="ThemeTokens"/> so the stage and the
    /// overlay counter both resolve the Deep-Indigo tokens.
    /// </summary>
    internal static StackControl BuildPresentRoot()
        => Controls.Stack
            .WithWidth("100%")
            .WithStyle(
                ThemeTokens +
                "position: relative; width: 100%; height: 100%; min-height: 100vh; box-sizing: border-box; " +
                "display: flex; align-items: center; justify-content: center; " +
                "padding: 1.5vmin; background: var(--ae-bg-solid);");

    /// <summary>The Present-mode corner counter chip — <c>var(--ae-fg)</c> on a translucent pill, readable on the dark stage.</summary>
    internal static UiControl BuildPresentCounter(int index, int count)
        => BuildCounter(index, count)
            .WithStyle(
                "position: absolute; right: 2vmin; bottom: 2vmin; padding: 4px 12px; " +
                "border-radius: 999px; background: rgba(0, 0, 0, 0.35); color: var(--ae-fg); " +
                "font-size: 13px; font-variant-numeric: tabular-nums; opacity: 0.8; pointer-events: none;");

    /// <summary>
    /// Builds the keyboard driver for a standalone slide's Present walk: Home/End target the first /
    /// last ordered slide, prev/next the neighbours, Esc the deck (parent) overview.
    /// </summary>
    private static SlideShowControl BuildSlideShowForSlide(
        string hubPath, MeshNode? prev, MeshNode? next, IReadOnlyList<MeshNode> slides)
    {
        var parentPath = GetParentPath(hubPath);
        return new SlideShowControl
        {
            FirstHref = slides.Count > 0 ? MeshNodeLayoutAreas.BuildUrl(slides[0].Path, PresentArea) : null,
            LastHref = slides.Count > 0 ? MeshNodeLayoutAreas.BuildUrl(slides[^1].Path, PresentArea) : null,
            PreviousHref = prev is null ? null : MeshNodeLayoutAreas.BuildUrl(prev.Path, PresentArea),
            NextHref = next is null ? null : MeshNodeLayoutAreas.BuildUrl(next.Path, PresentArea),
            ExitHref = parentPath is not null ? $"/{parentPath}" : $"/{hubPath}",
        };
    }

    // ── Notes ────────────────────────────────────────────────────────────────

    private static UiControl BuildNotes(LayoutAreaHost host, MeshNode? node)
    {
        var slide = node.ContentAs<SlideContent>(host.Hub.JsonSerializerOptions);

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        container = container.WithView(
            Controls.H1(node?.Name ?? node?.Id ?? "Speaker Notes").WithStyle("margin: 0 0 12px 0;"));

        container = container.WithView(
            Controls.Markdown(string.IsNullOrWhiteSpace(slide?.Notes)
                    ? "*No speaker notes for this slide.*"
                    : slide!.Notes!)
                .WithStyle("width: 100%; margin-bottom: 20px;"),
            NotesBodyArea);

        // Compact, non-interactive preview of the slide below the notes.
        container = container.WithView(
            Controls.Stack
                .WithStyle("max-width: 640px; font-size: 60%;")
                .WithView(BuildStage(slide, nextHref: null), StageArea));

        return container;
    }

    // ── Stage ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The slide stage: a 16:9 surface whose ONE surface IS the slide's own background — the slide
    /// body (markdown / HTML / SVG) renders directly onto it, never onto a white inner card. The
    /// markdown control's default <c>background-color: var(--neutral-layer-1)</c> is overridden to
    /// transparent, and the default text colour is the theme-INDEPENDENT <c>var(--ae-fg)</c>, so a
    /// slide is readable in any portal theme (no white-on-white, no dark-blue-on-black); a slide's
    /// own <see cref="SlideContent.Background"/> / inline element colours still win.
    /// <para>When <paramref name="present"/> the stage is sized to fill most of the viewport (a thin
    /// letterbox) with minimal chrome; otherwise it sits inside the page column. When
    /// <paramref name="nextHref"/> is set, clicking the stage advances by rendering a
    /// <see cref="RedirectControl"/> (standard navigation).</para>
    /// </summary>
    /// <param name="slide">The slide content to render on the stage.</param>
    /// <param name="nextHref">Where a stage click advances to, or null for no click-to-advance.</param>
    /// <param name="present">True for the full-bleed Present view; false for the in-page Content view.</param>
    internal static UiControl BuildStage(SlideContent? slide, string? nextHref, bool present = false)
    {
        var background = string.IsNullOrWhiteSpace(slide?.Background)
            ? DefaultBackground
            : slide!.Background!;

        // ONE surface: the markdown canvas is transparent and inherits the stage's colour, so the
        // slide HTML sits directly on the slide-owned backdrop (the white-inner-card fix).
        var body = Controls.Markdown(
                string.IsNullOrWhiteSpace(slide?.Content) ? EmptySlideHint : slide!.Content!)
            .WithStyle("width: 100%; background: transparent; background-color: transparent; color: inherit;");

        // Full-bleed sizing (Present): width follows the viewport height at 16:9 so the slide fills
        // most of the screen; a thin frame instead of the big letterbox/margin.
        var sizing = present
            ? "width: min(100%, calc((100vh - 3vmin) * 16 / 9)); max-height: 100%; " +
              "border-radius: 6px; box-shadow: 0 10px 44px rgba(0, 0, 0, 0.45); padding: 3.2% 6%; "
            : "width: 100%; border-radius: 14px; box-shadow: 0 14px 40px rgba(0, 0, 0, 0.28); padding: 3.6% 6.5%; ";

        var stage = Controls.Stack
            .WithWidth("100%")
            .WithView(body, SlideBodyArea)
            .WithStyle(
                ThemeTokens +
                "aspect-ratio: 16 / 9; box-sizing: border-box; " +
                "display: flex; flex-direction: column; justify-content: center; overflow: hidden; " +
                sizing +
                "font-size: clamp(16px, 2.2vw, 34px); line-height: 1.4; " +
                $"background: {background}; color: var(--ae-fg);" +
                (nextHref is null ? "" : " cursor: pointer;"));

        if (nextHref is not null)
            stage = stage.WithClickAction(ctx =>
            {
                // Standard navigation: replace this area with a RedirectControl —
                // the client navigates to the next slide (same mechanism as
                // CodeLayoutAreas' save-then-redirect). Sync lambda, no async.
                ctx.Host.UpdateArea(ctx.Area, new RedirectControl(nextHref));
                return Task.CompletedTask;
            });

        return stage;
    }

    private static LabelControl BuildCounter(int index, int count)
        => Controls.Body($"Slide {(index >= 0 ? index + 1 : 1)} / {Math.Max(count, 1)}");

    // ── Deck resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// Live observable of the deck's slides in play order. When this slide's PARENT is a
    /// <see cref="DeckNodeType">Deck</see> with a non-empty <see cref="DeckContent.Slides"/>
    /// manifest, the order comes from that EXTERNAL manifest — the deck owns the sequence,
    /// not the slides. Otherwise a deck is any parent whose children are Slide nodes and they
    /// play by <see cref="MeshNode.Order"/> (null last), ties broken by path. Starts with an
    /// empty list so the stage renders before the first query emission.
    /// </summary>
    private static IObservable<IReadOnlyList<MeshNode>> ObserveDeckSlides(LayoutAreaHost host)
    {
        var parentPath = GetParentPath(host.Hub.Address.ToString());
        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null || parentPath is null)
            return Observable.Return<IReadOnlyList<MeshNode>>([]);

        // Candidate set: the sibling Slide nodes sharing this node's parent.
        var siblingSlides = meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{parentPath} nodeType:{SlideNodeType.NodeType}"))
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
        var parentManifest = host.Workspace.GetMeshNodeStream(parentPath)
            .Select(parent => DeckManifestPaths(parent, parentPath, host))
            .StartWith((IReadOnlyList<string>?)null);

        return siblingSlides
            .CombineLatest(parentManifest, OrderSlides)
            .StartWith((IReadOnlyList<MeshNode>)ImmutableList<MeshNode>.Empty);
    }

    /// <summary>
    /// If <paramref name="parent"/> is a Deck with a non-empty manifest, returns its entries
    /// resolved to full child paths (the deck's declared order); otherwise <c>null</c> (→ the
    /// <see cref="MeshNode.Order"/> fallback).
    /// </summary>
    private static IReadOnlyList<string>? DeckManifestPaths(
        MeshNode? parent, string parentPath, LayoutAreaHost host)
    {
        if (parent is null || !string.Equals(parent.NodeType, DeckNodeType.NodeType, StringComparison.Ordinal))
            return null;
        var refs = parent.ContentAs<DeckContent>(host.Hub.JsonSerializerOptions)?.Slides;
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
    private static IReadOnlyList<MeshNode> OrderSlides(
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

    /// <summary>
    /// Finds this slide among the ordered deck slides and returns its index plus the
    /// previous / next sibling (null at either end or while the deck is still loading).
    /// </summary>
    private static (int Index, MeshNode? Prev, MeshNode? Next) Locate(
        string hubPath, IReadOnlyList<MeshNode> slides)
    {
        var index = -1;
        for (var i = 0; i < slides.Count; i++)
            if (string.Equals(slides[i].Path, hubPath, StringComparison.Ordinal))
            {
                index = i;
                break;
            }

        if (index < 0)
            return (-1, null, null);

        return (index,
            index > 0 ? slides[index - 1] : null,
            index < slides.Count - 1 ? slides[index + 1] : null);
    }

    private static string? GetParentPath(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx > 0 ? path[..idx] : null;
    }
}
