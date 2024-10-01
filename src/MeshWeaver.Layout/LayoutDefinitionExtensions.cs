using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

/// <summary>
/// Provides extension methods for working with layout definitions.
/// </summary>
public static class LayoutDefinitionExtensions
{
    /// <summary>
    /// Adds a view to the layout definition with the specified context and generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="context">The context function to determine when to render the view.</param>
    /// <param name="generator">The generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, IObservable<object>> generator
    ) =>
        layout.WithRenderer(context, (a, ctx, s) => a.RenderArea(ctx, generator.Invoke(a, ctx), s));

    /// <summary>
    /// Adds a view to the layout definition for the specified area and generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="area">The area to render the view.</param>
    /// <param name="generator">The generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, IObservable<object>> generator
    ) =>
        layout.WithView(c => c.Area == area, generator);

    /// <summary>
    /// Adds a view to the layout definition with the specified context and observable generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="context">The context function to determine when to render the view.</param>
    /// <param name="generator">The observable generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        IObservable<Func<LayoutAreaHost, RenderingContext, object>> generator
    ) =>
        WithView(layout,
            context,
            generator.Select(o =>
                (Func<LayoutAreaHost, RenderingContext, Task<object>>)((a, c) => Task.FromResult(o.Invoke(a, c)))
            )
        );

    /// <summary>
    /// Adds a view to the layout definition for the specified area and observable generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="area">The area to render the view.</param>
    /// <param name="generator">The observable generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        IObservable<Func<LayoutAreaHost, RenderingContext, object>> generator
    ) =>
        layout.WithView(c => c.Area == area, generator);

    /// <summary>
    /// Adds a view to the layout definition with the specified context and generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="context">The context function to determine when to render the view.</param>
    /// <param name="generator">The generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, Task<object>> generator
    ) =>
        layout.WithRenderer(context, (a, ctx, s) => a.RenderArea(ctx, generator.Invoke(a, ctx), s));
}
