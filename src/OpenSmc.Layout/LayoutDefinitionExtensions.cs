using System.Reactive.Linq;
using OpenSmc.Data;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public static class LayoutDefinitionExtensions{

    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, IObservable<object>> generator
    ) =>
        layout.WithRenderer(context, (a, ctx) => a.RenderArea(ctx, generator.Invoke(a,ctx)));

    public static LayoutDefinition WithView(this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, IObservable<object>> generator
    ) =>
        layout.WithView(c => c.Area == area, generator);

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
    public static LayoutDefinition WithView(this LayoutDefinition layout,Func<RenderingContext, bool> context, ViewDefinition generator) =>
        WithView(
            layout,
            context,
            Observable.Return(generator)
        );

    public static LayoutDefinition WithView(this LayoutDefinition layout, string area, ViewDefinition generator) =>
        layout.WithView(ctx => ctx.Area == area, generator);
    public static LayoutDefinition WithView<T>(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        IObservable<ViewDefinition<T>> generator
    ) =>
        layout.WithRenderer(context, (a, c) => a.RenderArea(c, generator));

    public static LayoutDefinition WithView<T>(this LayoutDefinition layout,
        string area,
        IObservable<ViewDefinition<T>> generator
    ) =>
        layout.WithView(c => c.Area == area, generator);


    public static LayoutDefinition WithView(this LayoutDefinition layout,Func<RenderingContext, bool> context, object view) =>
        layout.WithRenderer(context, (a, c) => a.RenderArea(c, view));

    public static LayoutDefinition WithView(this LayoutDefinition layout, string area, object view) =>
        layout.WithView(c => c.Area == area, view);
    public static LayoutDefinition WithView(this LayoutDefinition layout,
        Func<RenderingContext, bool> context,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<object>> view)
        => layout.WithRenderer(context, (a, c) => a.RenderArea(c, view.Invoke));

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





    public static EntityStore UpdateControl(this EntityStore store, string id, UiControl control)
        => store.Update(
            LayoutAreaReference.Areas,
            i => i.SetItem(id, control)
        );
    public static EntityStore UpdateData(this EntityStore store, string id, object control)
        => store.Update(
            LayoutAreaReference.Data,
            i => i.SetItem(id, control)
        );

    public static LayoutDefinition WithRenderer(this LayoutDefinition layout,
        Func<RenderingContext, bool> filter,
        Func<LayoutAreaHost, RenderingContext, IEnumerable<Func<EntityStore,EntityStore>>> renderer)
        => layout.WithRenderer(filter, renderer.Invoke);
}
