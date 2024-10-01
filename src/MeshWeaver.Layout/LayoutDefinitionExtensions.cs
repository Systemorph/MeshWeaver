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
    /// Synchronous View
    /// </summary>
    /// <param name="layout">The layout definition</param>
    /// <param name="area">The area of the view</param>
    /// <param name="generator">The generator of the view</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, object> generator
    ) => layout.WithView(ctx => ctx.Area == area, generator);

    /// <summary>
    /// Synchronous View
    /// </summary>
    /// <param name="layout">The layout definition</param>
    /// <param name="filter">The filter of the view</param>
    /// <param name="generator">The generator of the view</param>
    /// <returns></returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> filter,
        Func<LayoutAreaHost, RenderingContext, object> generator
    ) => layout.WithView(filter, (h,c, _) => Task.FromResult(generator.Invoke(h,c)));


    /// <summary>
    /// Adds a view to the layout definition with the specified context and observable generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="context">The context function to determine when to render the view.</param>
    /// <param name="generator">The observable generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        IObservable<Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<object>>> generator
    ) =>
        layout.WithRenderer(context, (host, ctx, store) => host.RenderArea(ctx, generator.Cast<ViewDefinition>(), store));

    /// <summary>
    /// Adds a view to the layout definition for the specified area and observable generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="area">The area to render the view.</param>
    /// <param name="view">The view to be rendered.</param>
    /// <returns>The updated layout definition.</returns>

    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        object view
    ) =>
        layout.WithView(c => c.Area == area, view);

    /// <summary>
    /// Adds a view to the layout definition for the specified area and observable generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="filter">The filter to render the view.</param>
    /// <param name="view">The view to be rendered.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> filter,
        object view
    ) =>
        layout.WithRenderer(filter, (h,c,s) => h.RenderArea(c, view, s));

    /// <summary>
    /// Adds a view to the layout definition with the specified context and generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="area">The area to render the view.</param>
    /// <param name="generator">The generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<object>> generator
    ) => layout.WithView(ctx => ctx.Area == area, generator);

    /// <summary>
    /// Adds a view to the layout definition with the specified context and generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="context">The context function to determine when to render the view.</param>
    /// <param name="generator">The generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext,  CancellationToken, Task<object>> generator
    ) =>
        layout.WithRenderer(context, (a, ctx, s) => a.RenderArea(ctx, generator.Invoke, s));
    public static EntityStoreAndUpdates UpdateControl(this EntityStore store, string id, UiControl control)
        => new([new EntityStoreUpdate(LayoutAreaReference.Areas, id, control)], store.Update(LayoutAreaReference.Areas, i => i.Update(id, control)));
    public static EntityStoreAndUpdates UpdateData(this EntityStore store, string id, object control)
        => new([new EntityStoreUpdate(LayoutAreaReference.Data, id, control)], store.Update(LayoutAreaReference.Areas, i => i.Update(id, control)));

}
