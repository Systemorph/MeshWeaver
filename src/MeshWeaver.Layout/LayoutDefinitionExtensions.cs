using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Data.Documentation;
using MeshWeaver.Layout.Composition;
using Microsoft.Extensions.DependencyInjection;
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
        Func<LayoutAreaHost, RenderingContext, IObservable<UiControl>> generator
    ) =>
        layout.WithRenderer(context, (a, ctx, s) => a.RenderArea(ctx, generator.Invoke(a, ctx), s));

    /// <summary>
    /// Adds a view to the layout definition for the specified area and generator.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <param name="area">The area to render the view.</param>
    /// <param name="generator">The generator function to produce the view content.</param>
    /// <param name="areaDefinition">Area definition exposed in layout area catalog</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, IObservable<UiControl>> generator,
        Func<LayoutAreaDefinition, LayoutAreaDefinition>? areaDefinition = null
    ) =>
        layout
            .WithView(c => c.Area == area, generator)
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
        WithView(layout,
            context,
            generator.Select(o =>
                (Func<LayoutAreaHost, RenderingContext, Task<UiControl>>)((a, c) => Task.FromResult(o.Invoke(a, c)))
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
        IObservable<Func<LayoutAreaHost, RenderingContext, UiControl>> generator
    )
        => layout.WithView(c => c.Area == area, generator);

    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        IObservable<Func<LayoutAreaHost, RenderingContext, Task<UiControl>>> generator
    ) => WithView(layout, context, generator.Cast<ViewDefinition<UiControl>>());

    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        IObservable<Func<LayoutAreaHost, RenderingContext, Task<object>>> generator
    ) => layout.WithView(c => c.Area == area, generator);

    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<T>> generator
    ) where T:UiControl => WithView(
        layout,
        context, 
        Observable.Return<ViewDefinition>((async (x, y, z) => await generator(x, y, z))));

    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<T>> generator
    ) where T : UiControl => layout.WithView(c => c.Area == area, generator);
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
                => a.RenderArea(c, generator, s))
        ;
 
    public static LayoutDefinition WithView<T>(this LayoutDefinition layout,
        string area,
        IObservable<ViewDefinition<T>> generator,
        Func<LayoutAreaDefinition, LayoutAreaDefinition>? areaDefinition = null
    ) where T:UiControl =>
        layout.WithView(c => c.Area == area, generator)
            .WithAreaDefinition(layout.CreateLayoutAreaDefinition(area, areaDefinition, null))
        ;

    private static LayoutAreaDefinition CreateLayoutAreaDefinition(this LayoutDefinition layout, string area, Func<LayoutAreaDefinition, LayoutAreaDefinition> options, Delegate delgate)
    {
        LayoutAreaDefinition ret = new(area, $"{layout.Hub.Address}/{area}");
        if (delgate is not null)
        {
            var method = delgate.Method;
            var doc = method.GetXmlDocsSummary();
            ret = ret.WithDescription(doc);
            if(method.GetCustomAttribute<DisplayAttribute>() is { } displayAttribute)
            {
                ret = ret with{Order = displayAttribute.Order};
                if(displayAttribute.Description is not null)
                    ret = ret.WithDescription(displayAttribute.Description);
                if(displayAttribute.Name is not null)
                    ret = ret.WithTitle(displayAttribute.Name);
            }
            if (method.GetCustomAttribute<BrowsableAttribute>() is { } browsableAtt)
                ret = ret with{IsInvisible = !browsableAtt.Browsable};
        }
        if (options is not null)
            ret = options.Invoke(ret);
        return ret;
    }

    public static LayoutDefinition WithView(
        this LayoutDefinition layout, 
        Func<RenderingContext, bool> context, 
        object view,
        LayoutAreaDefinition? layoutAreaDefinition = null) =>
        layout.WithRenderer(context, (a, c, s) => a.RenderArea(c, view, s))
            .WithAreaDefinition(layoutAreaDefinition);

    public static LayoutDefinition WithView(
        this LayoutDefinition layout, 
        string area, 
        UiControl view,
        Func<LayoutAreaDefinition, LayoutAreaDefinition>? areaDefinition = null
        ) =>
        layout
            .WithView(c => c.Area == area, view)
            .WithAreaDefinition(layout.CreateLayoutAreaDefinition(area, areaDefinition, null));

    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<UiControl>> view,
        LayoutAreaDefinition? areaDefinition = null)
        => layout
            .WithRenderer(context,
                (a, c, s)
                    => a.RenderArea(c, view.Invoke, s))
            .WithAreaDefinition(areaDefinition);

    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<UiControl>> view)
        => layout.WithView(area, view, null);
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<UiControl>> view,
        Func<LayoutAreaDefinition, LayoutAreaDefinition> areaDefinition)
        => layout.WithView(c => c.Area == area, view)
            .WithAreaDefinition(layout.CreateLayoutAreaDefinition(area, areaDefinition, view));

    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, T> view,
        LayoutAreaDefinition? areaDefinition = null) where T:UiControl
        => layout
            .WithView(context, 
                (a, ctx) 
                    => Observable.Return<UiControl>(view(a, ctx)))
            .WithAreaDefinition(areaDefinition);

    public static LayoutDefinition WithView<T>(
        this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, T> view,
        Func<LayoutAreaDefinition, LayoutAreaDefinition>? areaDefinition = null
    ) where T : UiControl => layout.WithView(c => c.Area == area, view)
        .WithAreaDefinition(layout.CreateLayoutAreaDefinition(area, areaDefinition, view));

    public static EntityStoreAndUpdates UpdateControl(this EntityStore store, string id, UiControl control)
        => new (store.Update(LayoutAreaReference.Areas, i => i.Update(id, control)), [new EntityUpdate(LayoutAreaReference.Areas, id, control)], null);
    public static EntityStoreAndUpdates UpdateData(this EntityStore store, string id, object control)
        => new(store.Update(LayoutAreaReference.Data, i => i.Update(id, control)), [new EntityUpdate(LayoutAreaReference.Data, id, control)], null);

    public static LayoutDefinition WithRenderer(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> filter,
        Func<LayoutAreaHost, RenderingContext, EntityStore, EntityStoreAndUpdates> renderer)
        => layout.WithRenderer(filter, renderer.Invoke);
}
