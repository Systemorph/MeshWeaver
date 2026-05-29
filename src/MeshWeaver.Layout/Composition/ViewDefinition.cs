using MeshWeaver.Data;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout.Composition;

public delegate Task<T?> ViewDefinition<T>(LayoutAreaHost area, RenderingContext context, CancellationToken cancellationToken) where T : UiControl?;
public delegate Task<UiControl?> ViewDefinition(LayoutAreaHost area, RenderingContext context, CancellationToken cancellationToken);

public record RenderingContext(string Area)
{
    public string? Layout { get; init; }
    public string? DataContext { get; init; }
    public string DisplayName { get; init; } = Area.Wordify() ?? Area;
    public RenderingContext? Parent { get; init; }

    /// <summary>
    /// Nesting depth of this context — 0 for a top-level area, incremented by
    /// <c>GetContextForArea</c> for every nested child area. The render path
    /// (<c>LayoutAreaHost.RenderArea</c>) uses this as a recursion guard: a
    /// self-referential / cyclic control tree would otherwise recurse until the
    /// stack overflows, which is a fatal, uncatchable process crash.
    /// </summary>
    public int Depth { get; init; }

    public static implicit operator RenderingContext(string s) => new(s);
};

public delegate IObservable<T> ViewStream<out T>(LayoutAreaHost area, RenderingContext context, EntityStore store) where T : UiControl?;
public delegate Task<IObservable<T>> AsyncViewStream<T>(LayoutAreaHost area, RenderingContext context, EntityStore store, CancellationToken ct) where T : UiControl?;


