using MeshWeaver.Data;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout.Composition;

/// <summary>
/// A delegate that asynchronously produces a nullable control of type <typeparamref name="T"/> for a layout area.
/// </summary>
/// <typeparam name="T">The UI control type to produce; must be nullable.</typeparam>
public delegate Task<T?> ViewDefinition<T>(LayoutAreaHost area, RenderingContext context, CancellationToken cancellationToken) where T : UiControl?;
/// <summary>
/// A non-generic delegate that asynchronously produces a nullable <see cref="UiControl"/> for a layout area.
/// </summary>
public delegate Task<UiControl?> ViewDefinition(LayoutAreaHost area, RenderingContext context, CancellationToken cancellationToken);

/// <summary>
/// Carries the contextual metadata for a single layout area render: its name, layout, data context, display name,
/// parent context, and recursion depth.
/// </summary>
/// <param name="Area">The canonical area name used to look up the view definition and register the rendered output.</param>
public record RenderingContext(string Area)
{
    /// <summary>The layout name that scopes which view definitions are eligible for this area, or null for the default layout.</summary>
    public string? Layout { get; init; }
    /// <summary>An optional data-context path that narrows the data source for data-bound controls in this area.</summary>
    public string? DataContext { get; init; }
    /// <summary>A human-readable label derived from <see cref="Area"/> via wordification, used in headings and breadcrumbs.</summary>
    public string DisplayName { get; init; } = Area.Wordify() ?? Area;
    /// <summary>The parent rendering context, forming a chain that reflects the control tree hierarchy.</summary>
    public RenderingContext? Parent { get; init; }

    /// <summary>
    /// Nesting depth of this context — 0 for a top-level area, incremented by
    /// <c>GetContextForArea</c> for every nested child area. The render path
    /// (<c>LayoutAreaHost.RenderArea</c>) uses this as a recursion guard: a
    /// self-referential / cyclic control tree would otherwise recurse until the
    /// stack overflows, which is a fatal, uncatchable process crash.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>Creates a <see cref="RenderingContext"/> from a plain area name string.</summary>
    /// <param name="s">The area name.</param>
    /// <returns>A new <see cref="RenderingContext"/> with <see cref="Area"/> set to <paramref name="s"/>.</returns>
    public static implicit operator RenderingContext(string s) => new(s);
};

/// <summary>
/// A delegate that synchronously produces an observable stream of controls of type <typeparamref name="T"/> for a layout area.
/// </summary>
/// <typeparam name="T">The UI control type emitted by the stream; must be nullable.</typeparam>
public delegate IObservable<T> ViewStream<out T>(LayoutAreaHost area, RenderingContext context, EntityStore store) where T : UiControl?;
/// <summary>
/// A delegate that asynchronously produces an observable stream of controls of type <typeparamref name="T"/> for a layout area.
/// </summary>
/// <typeparam name="T">The UI control type emitted by the stream; must be nullable.</typeparam>
public delegate Task<IObservable<T>> AsyncViewStream<T>(LayoutAreaHost area, RenderingContext context, EntityStore store, CancellationToken ct) where T : UiControl?;


