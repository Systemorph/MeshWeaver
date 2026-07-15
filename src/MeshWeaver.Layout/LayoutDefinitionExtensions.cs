using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using Namotion.Reflection;

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
        Func<LayoutAreaHost, RenderingContext, IObservable<UiControl?>> generator
    ) =>
        layout.WithRenderer(context, (a, ctx, s) => a.RenderAreaObservable(ctx, generator.Invoke(a, ctx), s));


    /// <summary>
    /// Adds a view to the layout definition for the specified area and generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="area">The area name to render the view.</param>
    /// <param name="generator">The generator function to produce the view content.</param>
    /// <param name="areaDefinition">Area definition exposed in layout area catalog</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<IObservable<T?>>> generator,
        Func<LayoutAreaDefinition, LayoutAreaDefinition>? areaDefinition = null
    ) where T : UiControl => layout.WithNamedRenderer(area, (host, ctx, s) => host
            .RenderArea(ctx, generator, s))
        .WithAreaDefinition(
            layout.CreateLayoutAreaDefinition(area, areaDefinition, generator)
        );

    /// <summary>
    /// Adds a view to the layout definition for the specified area and generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="context">The context to render the view.</param>
    /// <param name="generator">The generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<IObservable<T?>>> generator
    ) where T : UiControl => layout.WithRenderer(context, (host, ctx, s) => host.RenderArea(ctx, generator, s));

    /// <summary>
    /// Adds a view to the layout definition for the specified area and generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="area">The area to render the view.</param>
    /// <param name="generator">The generator function to produce the view content.</param>
    /// <param name="areaDefinition">Area definition exposed in layout area catalog</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView<T>(this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, IObservable<T?>> generator,
        Func<LayoutAreaDefinition, LayoutAreaDefinition>? areaDefinition = null
    ) where T : UiControl =>
        layout
            .WithNamedRenderer(area, (a, ctx, s) => a.RenderAreaObservable(ctx, generator.Invoke(a, ctx), s))
            .WithAreaDefinition(
                layout.CreateLayoutAreaDefinition(area, areaDefinition, generator)
            );


    /// <summary>
    /// Adds a view to the layout definition with the specified context and observable generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="context">The context function to determine when to render the view.</param>
    /// <param name="generator">The observable generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        IObservable<Func<LayoutAreaHost, RenderingContext, UiControl>> generator
    ) =>
        // The generator is a SYNCHRONOUS view factory — render it through the synchronous
        // RenderAreaObservable path directly (mirrors the string-area overload below), rather
        // than wrapping each invocation in Task.FromResult to satisfy the Task<UiControl> shape.
        layout.WithRenderer(context, (a, c, s) =>
            a.RenderAreaObservable(c, generator.Select(o => (object?)o.Invoke(a, c)), s));

    /// <summary>
    /// Adds a view to the layout definition for the specified area and observable generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="area">The area to render the view.</param>
    /// <param name="generator">The observable generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        IObservable<Func<LayoutAreaHost, RenderingContext, UiControl>> generator
    )
        => layout.WithNamedRenderer(area, (a, c, s) =>
            a.RenderAreaObservable(c, generator.Select(o => (object?)o.Invoke(a, c)), s));

    /// <summary>
    /// Adds a view to the layout definition with the specified context and an observable stream of
    /// async UiControl generators; each emitted generator is invoked to produce the view content.
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="context">Predicate that selects the rendering context for this view.</param>
    /// <param name="generator">Observable stream of async view-factory functions.</param>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        IObservable<Func<LayoutAreaHost, RenderingContext, Task<UiControl>>> generator
    ) => WithView(layout, context, generator.Cast<ViewDefinition<UiControl>>());

    /// <summary>
    /// Adds a view to the layout definition for the specified area using an observable stream of
    /// async object-returning generators; each emitted generator produces the area content.
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="area">The area name to render the view into.</param>
    /// <param name="generator">Observable stream of async view-factory functions returning object.</param>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        IObservable<Func<LayoutAreaHost, RenderingContext, Task<object>>> generator
    ) => layout.WithNamedRenderer(area, (a, c, s) =>
            a.RenderAreaObservable(c, generator.Cast<ViewDefinition>(), s));

    /// <summary>
    /// Adds a context-matched view whose content is produced by a single async generator function.
    /// </summary>
    /// <typeparam name="T">The UiControl subtype returned by the generator.</typeparam>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="context">Predicate that selects the rendering context for this view.</param>
    /// <param name="generator">The async function that produces the view content.</param>
    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<T>> generator
    ) where T : UiControl? => WithView(
        layout,
        context,
        Observable.Return<ViewDefinition>((async (x, y, z) => await generator(x, y, z))));

    /// <summary>
    /// Adds a named-area view whose content is produced by a single async generator function.
    /// </summary>
    /// <typeparam name="T">The UiControl subtype returned by the generator.</typeparam>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="area">The area name to render the view into.</param>
    /// <param name="generator">The async function that produces the view content.</param>
    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<T>> generator
    ) where T : UiControl? => layout.WithNamedRenderer(area, (a, c, s) =>
            a.RenderAreaObservable(c, Observable.Return<ViewDefinition>(async (x, y, z) => await generator(x, y, z)), s));
    /// <summary>
    /// Adds a context-matched view produced by a single ViewDefinition (async factory function).
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="context">Predicate that selects the rendering context for this view.</param>
    /// <param name="generator">The ViewDefinition factory invoked once to produce the view content.</param>
    public static LayoutDefinition WithView(this LayoutDefinition layout, Func<RenderingContext, bool> context, ViewDefinition generator) =>
        WithView(
            layout,
            context,
            Observable.Return(generator)
        );


    /// <summary>
    /// Adds a view to the layout definition with the specified context and generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="context">The context function to determine when to render the view.</param>
    /// <param name="generator">The generator function to produce the view content.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        IObservable<ViewDefinition> generator) =>
        layout.WithRenderer(context,
            (a, c, s)
                => a.RenderAreaObservable(c, generator, s))
        ;

    /// <summary>
    /// Adds a named-area view driven by an observable stream of typed ViewDefinition factories;
    /// each emitted factory is bridged reactively and its result rendered into the area.
    /// </summary>
    /// <typeparam name="T">The UiControl subtype produced by each ViewDefinition.</typeparam>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="area">The area name to render the view into.</param>
    /// <param name="generator">Observable stream of typed ViewDefinition factories.</param>
    /// <param name="areaDefinition">Optional transform applied to the generated LayoutAreaDefinition.</param>
    public static LayoutDefinition WithView<T>(this LayoutDefinition layout,
        string area,
        IObservable<ViewDefinition<T>> generator,
        Func<LayoutAreaDefinition, LayoutAreaDefinition>? areaDefinition = null
    ) where T : UiControl? =>
        layout.WithNamedRenderer(area, (a, c, s) =>
                a.RenderAreaObservable(c,
                    generator.Select(vd => LayoutAreaHost.FromViewBuilder(ct => vd.Invoke(a, c, ct)).Select(x => (object?)x)).Switch(),
                    s))
            .WithAreaDefinition(layout.CreateLayoutAreaDefinition(area, areaDefinition, null))
        ;

    private static LayoutAreaDefinition CreateLayoutAreaDefinition(this LayoutDefinition layout, string area, Func<LayoutAreaDefinition, LayoutAreaDefinition>? options, Delegate? delgate)
    {
        LayoutAreaDefinition ret = new(area, $"{layout.Hub.Address}/{area}");
        if (delgate is not null)
        {
            var method = delgate.Method;
            var doc = MeshWeaver.Messaging.Serialization.XmlDocs.Summary(method);
            ret = ret.WithDescription(doc);

            // Check class-level DisplayAttribute for GroupName and fallback Order
            var declaringType = method.DeclaringType;
            if (declaringType?.GetCustomAttribute<DisplayAttribute>() is { } classDisplayAttribute)
            {
                if (classDisplayAttribute.GroupName is not null)
                    ret = ret.WithGroup(classDisplayAttribute.GroupName);
                if (ret.Order is null or 0 && classDisplayAttribute.GetOrder() is not null and not 0)
                    ret = ret with { Order = classDisplayAttribute.Order };
            }

            // Check method-level DisplayAttribute (takes precedence)
            if (method.GetCustomAttribute<DisplayAttribute>() is { } methodDisplayAttribute)
            {
                ret = ret with { Order = methodDisplayAttribute.GetOrder() ?? int.MaxValue };
                if (methodDisplayAttribute.Description is not null)
                    ret = ret.WithDescription(methodDisplayAttribute.Description);
                if (methodDisplayAttribute.Name is not null)
                    ret = ret.WithTitle(methodDisplayAttribute.Name);
                if (methodDisplayAttribute.GroupName is not null)
                    ret = ret.WithGroup(methodDisplayAttribute.GroupName);
            }

            if (method.GetCustomAttribute<BrowsableAttribute>() is { } browsableAtt)
                ret = ret with { IsInvisible = !browsableAtt.Browsable };
        }
        if (options is not null)
            ret = options.Invoke(ret);
        return ret;
    }

    /// <summary>
    /// Adds a context-matched view backed by a static pre-built view object (rendered once).
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="context">Predicate that selects the rendering context for this view.</param>
    /// <param name="view">The pre-built view object to render (e.g. a UiControl instance).</param>
    /// <param name="layoutAreaDefinition">Optional area definition to register alongside the renderer.</param>
    public static LayoutDefinition WithView(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        object view,
        LayoutAreaDefinition? layoutAreaDefinition = null) =>
        layout.WithRenderer(context, (a, c, s) => a.RenderAreaObservable(c, view, s))
            .WithAreaDefinition(layoutAreaDefinition!);
    /// <summary>
    /// Adds a context-matched view whose content is produced by an async generator that returns a
    /// reactive observable of control values.
    /// </summary>
    /// <typeparam name="T">The UiControl subtype emitted by the inner observable.</typeparam>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="context">Predicate that selects the rendering context for this view.</param>
    /// <param name="generator">Async factory that resolves to an observable of view controls.</param>
    /// <param name="layoutAreaDefinition">Optional area definition to register alongside the renderer.</param>
    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<IObservable<T?>>> generator,
        LayoutAreaDefinition? layoutAreaDefinition = null) =>
        layout.WithRenderer(context, (a, c, s) => a.RenderArea(c, generator, s))
            .WithAreaDefinition(layoutAreaDefinition!);

    /// <summary>
    /// Adds a named-area view backed by a static UiControl instance (rendered once into the area).
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="area">The area name to render the view into.</param>
    /// <param name="view">The UiControl to render; null clears the area.</param>
    /// <param name="areaDefinition">Optional transform applied to the generated LayoutAreaDefinition.</param>
    public static LayoutDefinition WithView(
        this LayoutDefinition layout,
        string area,
        UiControl? view,
        Func<LayoutAreaDefinition, LayoutAreaDefinition>? areaDefinition = null
        ) =>
        layout
            .WithNamedRenderer(area, (a, c, s) => a.RenderAreaObservable(c, view!, s))
            .WithAreaDefinition(layout.CreateLayoutAreaDefinition(area, areaDefinition, null));

    /// <summary>
    /// Adds a context-matched view produced by a single async UiControl-returning factory.
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="context">Predicate that selects the rendering context for this view.</param>
    /// <param name="view">The async factory that produces the UiControl to render.</param>
    /// <param name="areaDefinition">Optional area definition to register alongside the renderer.</param>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<UiControl?>> view,
        LayoutAreaDefinition? areaDefinition = null)
        => layout
            .WithRenderer(context,
                (a, c, s)
                    => a.RenderAreaObservable(c, (ViewDefinition)view.Invoke!, s))
            .WithAreaDefinition(areaDefinition!);

    /// <summary>
    /// Adds a named-area view produced by a single async UiControl-returning factory (no area-definition customization).
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="area">The area name to render the view into.</param>
    /// <param name="view">The async factory that produces the UiControl to render.</param>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<UiControl?>> view)
        => layout.WithView(area, view, null);
    /// <summary>
    /// Adds a named-area view produced by a single async UiControl-returning factory, with an
    /// optional area-definition customization.
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="area">The area name to render the view into.</param>
    /// <param name="view">The async factory that produces the UiControl to render.</param>
    /// <param name="areaDefinition">Optional transform applied to the generated LayoutAreaDefinition.</param>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<UiControl?>> view,
        Func<LayoutAreaDefinition, LayoutAreaDefinition>? areaDefinition)
        => layout.WithNamedRenderer(area, (a, c, s) => a.RenderAreaObservable(c, (ViewDefinition)view.Invoke!, s))
            .WithAreaDefinition(layout.CreateLayoutAreaDefinition(area, areaDefinition, view));

    /// <summary>
    /// Adds a context-matched view produced by a synchronous factory function.
    /// </summary>
    /// <typeparam name="T">The UiControl subtype returned by the factory.</typeparam>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="context">Predicate that selects the rendering context for this view.</param>
    /// <param name="view">The synchronous factory that produces the UiControl.</param>
    /// <param name="areaDefinition">Optional area definition to register alongside the renderer.</param>
    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, T> view,
        LayoutAreaDefinition? areaDefinition = null) where T : UiControl?
        => layout
            .WithView(context,
                (a, ctx)
                    => Observable.Return<UiControl?>(view(a, ctx)))
            .WithAreaDefinition(areaDefinition);

    /// <summary>
    /// Adds a named-area view produced by a synchronous factory function, with an optional
    /// area-definition customization.
    /// </summary>
    /// <typeparam name="T">The UiControl subtype returned by the factory.</typeparam>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="area">The area name to render the view into.</param>
    /// <param name="view">The synchronous factory that produces the UiControl.</param>
    /// <param name="areaDefinition">Optional transform applied to the generated LayoutAreaDefinition.</param>
    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, T> view,
        Func<LayoutAreaDefinition, LayoutAreaDefinition>? areaDefinition = null
    ) where T : UiControl? => layout.WithNamedRenderer(area, (a, ctx, s) => a.RenderAreaObservable(ctx, Observable.Return<UiControl?>(view(a, ctx)), s))
        .WithAreaDefinition(layout.CreateLayoutAreaDefinition(area, areaDefinition, view));

    /// <summary>
    /// Returns an EntityStoreAndUpdates that sets the control at <paramref name="id"/> in the
    /// Areas collection to <paramref name="control"/>.
    /// </summary>
    /// <param name="store">The current EntityStore snapshot.</param>
    /// <param name="id">The area key to update.</param>
    /// <param name="control">The UiControl to place at the key; null removes the control.</param>
    public static EntityStoreAndUpdates UpdateControl(this EntityStore store, string id, UiControl? control)
        => new(store.Update(LayoutAreaReference.Areas, i => i.Update(id, control!)), [new EntityUpdate(LayoutAreaReference.Areas, id, control!)], null);
    /// <summary>
    /// Returns an EntityStoreAndUpdates that sets the data item at <paramref name="id"/> in the
    /// Data collection to <paramref name="control"/>.
    /// </summary>
    /// <param name="store">The current EntityStore snapshot.</param>
    /// <param name="id">The data key to update.</param>
    /// <param name="control">The value to store at the key.</param>
    public static EntityStoreAndUpdates UpdateData(this EntityStore store, string id, object control)
        => new(store.Update(LayoutAreaReference.Data, i => i.Update(id, control)), [new EntityUpdate(LayoutAreaReference.Data, id, control)], null);

    /// <summary>
    /// Registers a low-level renderer function on the layout definition that is selected by
    /// <paramref name="filter"/> and produces an EntityStoreAndUpdates directly.
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <param name="filter">Predicate that selects the rendering context for this renderer.</param>
    /// <param name="renderer">The renderer function that merges area content into the EntityStore.</param>
    public static LayoutDefinition WithRenderer(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> filter,
        Func<LayoutAreaHost, RenderingContext, EntityStore, EntityStoreAndUpdates> renderer)
        => layout.WithRenderer(filter, renderer.Invoke);
}
