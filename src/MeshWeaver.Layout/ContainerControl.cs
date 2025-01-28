using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;
/// <summary>
/// Represents a container control that can hold multiple named area controls.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/container">Fluent UI Blazor Container documentation</a>.
/// </remarks>

public interface IContainerControl : IUiControl
{
    /// <summary>
    /// Gets the collection of named area controls within the container.
    /// </summary>
    IReadOnlyCollection<NamedAreaControl> Areas { get; }
}
/// <summary>
/// Abstract base class for container controls.
/// </summary>
/// <typeparam name="TControl">The type of the container control.</typeparam>
/// <param name="ModuleName">The name of the module.</param>
/// <param name="ApiVersion">The API version.</param>
public abstract record ContainerControl<TControl>(string ModuleName, string ApiVersion)
    : UiControl<TControl>(ModuleName, ApiVersion), IContainerControl
    where TControl : ContainerControl<TControl>
{
    internal const string Root = "";
    /// <summary>
    /// Gets the list of renderers for the container control.
    /// </summary>
    protected ImmutableList<Renderer> Renderers { get; init; } = ImmutableList<Renderer>.Empty;
    /// <summary>
    /// Generates an automatic name for a new area control.
    /// </summary>
    /// <returns>A string representing the automatic name.</returns>
    protected string GetAutoName() => $"{Renderers.Count + 1}";
    IReadOnlyCollection<NamedAreaControl> IContainerControl.Areas => Areas;
    /// <summary>
    /// Gets the collection of named area controls within the container.
    /// </summary>
    public ImmutableList<NamedAreaControl> Areas { get; init; } = ImmutableList<NamedAreaControl>.Empty;
    /// <summary>
    /// Gets a named area control with the specified options.
    /// </summary>
    /// <param name="options">A function to configure the named area control.</param>
    /// <returns>A configured <see cref="NamedAreaControl"/> instance.</returns>
    protected virtual NamedAreaControl GetNamedArea(Func<NamedAreaControl, NamedAreaControl> options)
    {
        return options.Invoke(new(null) { Id = GetAutoName() });
    }
     /// <summary>
    /// Adds a view to the container control with the specified options.
    /// </summary>
    /// <param name="view">The view to add.</param>
    /// <param name="options">A function to configure the named area control.</param>
    /// <returns>A new <typeparamref name="TControl"/> instance with the specified view and options.</returns>
    public TControl WithView(object view, Func<NamedAreaControl, NamedAreaControl> options)
    {
        var area = GetNamedArea(options);
        return This with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context, store) => host.RenderArea(GetContextForArea(context, area.Id.ToString()), view, store))
        };
    }


    public TControl WithView(object view) =>
        WithView(view, opt => opt.WithId(GetAutoName()));
    public TControl WithView(object view, string area) =>
        WithView(view, opt => opt.WithId(area));
    public TControl WithView<T>(ViewDefinition<T> viewDefinition) =>
        WithView(Observable.Return(viewDefinition), x => x);
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, T> viewDefinition, string area) =>
        WithView((h, c, _) => viewDefinition.Invoke(h, c), x => x.WithId(area));
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, T> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options) =>
        WithView((h, c, _) => viewDefinition.Invoke(h, c), options);


    public TControl WithView<T>(IObservable<ViewDefinition<T>> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options)
    {
        var area = Evaluate(options);
        return This with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context, store) =>
                host.RenderArea(GetContextForArea(context, area.Id.ToString()), viewDefinition, store))
        };
    }

    private NamedAreaControl Evaluate(Func<NamedAreaControl, NamedAreaControl> area)
    {
        return area.Invoke(new(null) { Id = GetAutoName() });
    }

    public TControl WithView(IObservable<ViewDefinition> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options)
    {
        var area = Evaluate(options);
        return This with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context, store) =>
                host.RenderArea(GetContextForArea(context, area.Id.ToString()), viewDefinition, store))
        };
    }

    public TControl WithView(IObservable<ViewDefinition> viewDefinition, string area) =>
        WithView(viewDefinition, control => control.WithId(area));
    public TControl WithView(IObservable<object> viewDefinition) =>
        WithView(viewDefinition, x => x);
    public TControl WithView(IObservable<object> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options)
    {
        var area = Evaluate(options);
        return This with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context, store) =>
                host.RenderArea(GetContextForArea(context, area.Id.ToString()), viewDefinition, store))
        };
    }

    public TControl WithView(IObservable<object> viewDefinition, string area) =>
        WithView(viewDefinition, control => control.WithId(area));
    public TControl WithView(IObservable<ViewDefinition> viewDefinition)
        => WithView(viewDefinition, x => x);

    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, T> viewDefinition)
        => WithView((h, c, _) => viewDefinition.Invoke(h, c), x => x);
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, Task<T>> viewDefinition)
        => WithView((h, c, _) => viewDefinition.Invoke(h, c), x => x);
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition)
        => WithView((h,c,_) => viewDefinition.Invoke(h,c), x => x);
    public TControl WithView<T>(ViewStream<T> viewDefinition)
        => WithView(viewDefinition, x => x);
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options)
        => WithView((h, c, _) => viewDefinition.Invoke(h, c), options);
    public TControl WithView<T>(ViewStream<T> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options)
    {
        var area = Evaluate(options);

        return This with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context, store) =>
                host.RenderArea(GetContextForArea(context, area.Id.ToString()), 
                    viewDefinition.Invoke, store))
        };
    }

    public TControl WithView<T>(ViewStream<T> viewDefinition, string area)
        => WithView(viewDefinition, control => control.WithId(area));

    public TControl WithView(Func<LayoutAreaHost, RenderingContext, EntityStore, object> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options)
        => WithView((la, ctx, s) => Observable.Return(viewDefinition.Invoke(la, ctx,s)), options);
    public TControl WithView(Func<LayoutAreaHost, RenderingContext, EntityStore, object> viewDefinition, string area)
        => WithView(viewDefinition, control => control.WithId(area));
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, EntityStore, IObservable<T>> viewDefinition, string area)
        => WithView(viewDefinition, control => control.WithId(area));
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition, string area)
        => WithView((h,c,_) => viewDefinition.Invoke(h,c), control => control.WithId(area));
    public TControl WithView(Func<LayoutAreaHost, RenderingContext, object> viewDefinition)
        => WithView((h,c,_) => viewDefinition.Invoke(h,c), x => x);

    protected override EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store) =>
        Renderers.Aggregate(base.Render(host, context, store),
            (acc, r) =>
            {
                var newRender = r.Invoke(host, context, acc.Store);
                return new EntityStoreAndUpdates(newRender.Store, acc.Updates.Concat(newRender.Updates), null);
            });
    protected override EntityStoreAndUpdates RenderSelf(LayoutAreaHost host, RenderingContext context, EntityStore store) => 
        store.UpdateControl(context.Area, PrepareRendering(context));

    protected override  TControl PrepareRendering(RenderingContext context)
    {
        return base.PrepareRendering(context) with
            { Areas = Areas.Select(a => a with { Area = $"{context.Area}/{a.Id}" }).ToImmutableList() };
    }


    public virtual bool Equals(ContainerControl<TControl> other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other))
            return true;
        return base.Equals(other) &&
               Skins.SequenceEqual(other.Skins) &&
               Areas.SequenceEqual(other.Areas);

    }


    public override int GetHashCode()
    {
        return HashCode.Combine(
            base.GetHashCode(),
            HashCode.Combine(
                Renderers.Aggregate(0, (acc, renderer) => acc ^ renderer.GetHashCode()),
                Skins.Aggregate(0, (acc, renderer) => acc ^ renderer.GetHashCode())
            ),
            HashCode.Combine(
                Renderers.Aggregate(0, (acc, area) => acc ^ area.GetHashCode()),
                Skins.Aggregate(0, (acc, rawArea) => acc ^ rawArea.GetHashCode())
            )
        );
    }

}

public abstract record ContainerControl<TControl, TSkin>(string ModuleName, string ApiVersion, TSkin Skin)
: ContainerControl<TControl>(ModuleName, ApiVersion)
    where TControl : ContainerControl<TControl, TSkin>
    where TSkin : Skin
{


    protected override TControl PrepareRendering(RenderingContext context)
    {
        return base.PrepareRendering(context)
            with
            { Skins = Skins?.RemoveAll(t => t is TSkin).Insert(0, Skin)  ?? [Skin] };
    }

    public TControl WithSkin(Func<TSkin, TSkin> skinConfig)
        => This with { Skin = skinConfig(Skin) };
}


public abstract record ContainerControlWithItemSkin<TControl,TSkin, TItemSkin>(string ModuleName, string ApiVersion, TSkin Skin)
    : ContainerControl<TControl, TSkin>(ModuleName, ApiVersion, Skin)
    where TControl : ContainerControlWithItemSkin<TControl, TSkin, TItemSkin>
    where TItemSkin: Skin
    where TSkin : Skin
{


    public TControl WithView<T>(T view, Func<TItemSkin, TItemSkin> options)
    => base.WithView(view, x => x.AddSkin(options.Invoke(CreateItemSkin(x))));




    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, EntityStore, T> viewDefinition, Func<TItemSkin, TItemSkin> options)
        => base.WithView((h, c, s) => viewDefinition.Invoke(h, c, s), x => x with { Skins = Skins.Add(options.Invoke(CreateItemSkin(x))) });
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, EntityStore, IObservable<T>> viewDefinition, Func<TItemSkin, TItemSkin> options)
        => WithView(viewDefinition.Invoke, x => x.AddSkin(options.Invoke(CreateItemSkin(x))));
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, T> viewDefinition, Func<TItemSkin, TItemSkin> options)
        => base.WithView((h, c, _) => viewDefinition.Invoke(h, c), x => 
            x.AddSkin(options.Invoke(CreateItemSkin(x))));
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition, Func<TItemSkin, TItemSkin> options)
        => WithView((h, c, _) => 
            viewDefinition.Invoke(h,c), x => x.AddSkin(options.Invoke(CreateItemSkin(x))));



    protected override NamedAreaControl GetNamedArea(Func<NamedAreaControl, NamedAreaControl> options)
    {
        var ret = base.GetNamedArea(options);
        if (ret.Skins == null || !ret.Skins.Any(s => s is TItemSkin))
            ret = ret.AddSkin(CreateItemSkin(ret));
        return ret;
    }

    protected abstract TItemSkin CreateItemSkin(NamedAreaControl namedArea);
}

