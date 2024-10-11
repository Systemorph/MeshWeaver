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
    )
        => layout.WithView(c => c.Area == area, generator);

    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        IObservable<Func<LayoutAreaHost, RenderingContext, Task<object>>> generator
    ) => WithView(layout, context, generator.Cast<ViewDefinition<object>>());

    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        IObservable<Func<LayoutAreaHost, RenderingContext, Task<object>>> generator
    ) => layout.WithView(c => c.Area == area, generator);

    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<T>> generator
    ) => WithView(layout,
        context, Observable.Return((ViewDefinition)(async (x, y, z) => await generator(x, y, z))));

    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<T>> generator
    ) => layout.WithView(c => c.Area == area, generator);
    public static LayoutDefinition WithView(this LayoutDefinition layout, Func<RenderingContext, bool> context, ViewDefinition generator) =>
        WithView(
            layout,
            context,
            Observable.Return(generator)
        );

    public static LayoutDefinition WithView(this LayoutDefinition layout, string area, ViewDefinition generator) =>
        layout.WithView(ctx => ctx.Area == area, generator);

    /// <summary>
    /// Adds a view to the layout definition with the specified context and generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="context">The context function to determine when to render the view.</param>
    /// <param name="generator">The generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView<T>(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        IObservable<ViewDefinition<T>> generator
    ) =>
        layout.WithRenderer(context, (a, c, s) => a.RenderArea(c, generator, s));

    public static LayoutDefinition WithView<T>(this LayoutDefinition layout,
        string area,
        IObservable<ViewDefinition<T>> generator
    ) =>
        layout.WithView(c => c.Area == area, generator);

    public static LayoutDefinition WithView(this LayoutDefinition layout, Func<RenderingContext, bool> context, object view) =>
        layout.WithRenderer(context, (a, c, s) => a.RenderArea(c, view, s));

    public static LayoutDefinition WithView(this LayoutDefinition layout, string area, object view) =>
        layout.WithView(c => c.Area == area, view);

    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<object>> view)
        => layout.WithRenderer(context, (a, c, s) => a.RenderArea(c, view.Invoke, s));

    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<object>> view)
        => layout.WithView(c => c.Area == area, view);

    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, T> view)
        => layout.WithView(context, (a, ctx) => Observable.Return<object>(view(a, ctx)));

    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, T> view
    ) => layout.WithView(c => c.Area == area, view);

    public static EntityStoreAndUpdates UpdateControl(this EntityStore store, string id, UiControl control)
        => new (store.Update(LayoutAreaReference.Areas, i => i.Update(id, control)), [new EntityStoreUpdate(LayoutAreaReference.Areas, id, control)]);
    public static EntityStoreAndUpdates UpdateData(this EntityStore store, string id, object control)
        => new(store.Update(LayoutAreaReference.Data, i => i.Update(id, control)), [new EntityStoreUpdate(LayoutAreaReference.Data, id, control)]);

    public static LayoutDefinition WithRenderer(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> filter,
        Func<LayoutAreaHost, RenderingContext, EntityStore, EntityStoreAndUpdates> renderer)
        => layout.WithRenderer(filter, renderer.Invoke);
}
