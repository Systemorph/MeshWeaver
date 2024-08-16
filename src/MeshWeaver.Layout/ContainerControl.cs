using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

public interface IContainerControl : IUiControl
{
    IReadOnlyCollection<NamedAreaControl> Areas { get; }
}
public abstract record ContainerControl<TControl>(string ModuleName, string ApiVersion)
    : UiControl<TControl>(ModuleName, ApiVersion), IContainerControl
    where TControl : ContainerControl<TControl>
{
    internal const string Root = "";
    protected ImmutableList<Renderer> Renderers { get; init; } = ImmutableList<Renderer>.Empty;
    protected string GetAutoName() => $"{Renderers.Count + 1}";
    IReadOnlyCollection<NamedAreaControl> IContainerControl.Areas => Areas;
    public ImmutableList<NamedAreaControl> Areas { get; init; } = ImmutableList<NamedAreaControl>.Empty;
    protected virtual NamedAreaControl GetNamedArea(Func<NamedAreaControl, NamedAreaControl> options)
    {
        return options.Invoke(new(null) { Id = GetAutoName() });
    }
    public TControl WithView(object view, Func<NamedAreaControl, NamedAreaControl> options)
    {
        var area = GetNamedArea(options);
        return This with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context) => host.RenderArea(GetContextForArea(context, area.Id.ToString()), view))
        };
    }


    public TControl WithView(object view) =>
        WithView(view, opt => opt.WithId(GetAutoName()));
    public TControl WithView(object view, string area) =>
        WithView(view, opt => opt.WithId(area));
    public TControl WithView<T>(ViewDefinition<T> viewDefinition) =>
        WithView(Observable.Return(viewDefinition), x => x);


    public TControl WithView<T>(IObservable<ViewDefinition<T>> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options)
    {
        var area = Evaluate(options);
        return This with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context) =>
                host.RenderArea(GetContextForArea(context, area.Id.ToString()), viewDefinition))
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
            Renderers = Renderers.Add((host, context) =>
                host.RenderArea(GetContextForArea(context, area.Id.ToString()), viewDefinition))
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
            Renderers = Renderers.Add((host, context) =>
                host.RenderArea(GetContextForArea(context, area.Id.ToString()), viewDefinition))
        };
    }

    public TControl WithView(IObservable<object> viewDefinition, string area) =>
        WithView(viewDefinition, control => control.WithId(area));
    public TControl WithView(IObservable<ViewDefinition> viewDefinition)
        => WithView(viewDefinition, x => x);

    public TControl WithView<T>(ViewStream<T> viewDefinition)
        => WithView(viewDefinition, x => x);
    public TControl WithView<T>(ViewStream<T> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options)
    {
        var area = Evaluate(options);

        return This with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context) =>
                host.RenderArea(GetContextForArea(context, area.Id.ToString()), viewDefinition.Invoke))
        };
    }

    public TControl WithView<T>(ViewStream<T> viewDefinition, string area)
        => WithView(viewDefinition, control => control.WithId(area));

    public TControl WithView(Func<LayoutAreaHost, RenderingContext, object> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options)
        => WithView((la, ctx) => Observable.Return(viewDefinition.Invoke(la, ctx)), options);
    public TControl WithView(Func<LayoutAreaHost, RenderingContext, object> viewDefinition, string area)
    => WithView(viewDefinition, control => control.WithId(area));
    public TControl WithView(Func<LayoutAreaHost, RenderingContext, object> viewDefinition)
        => WithView(viewDefinition, x => x);

    protected override IEnumerable<Func<EntityStore, EntityStore>> Render(LayoutAreaHost host, RenderingContext context) =>
        base.Render(host, context)
            .Concat(Renderers.SelectMany(r => (r.Invoke(host, context))));
    protected override Func<EntityStore, EntityStore> RenderSelf(LayoutAreaHost host, RenderingContext context)
        => store => store.UpdateControl(context.Area, PrepareRendering(context));

    protected override  TControl PrepareRendering(RenderingContext context)
    {
        return base.PrepareRendering(context) with
            { Areas = Areas.Select(a => a with { Area = $"{context.Area}/{a.Id}" }).ToImmutableList() };
    }

    protected static RenderingContext GetContextForArea(RenderingContext context, string area)
    {
        return context with { Area = $"{context.Area}/{area}", Parent = context };
    }

    public virtual bool Equals(TControl other)
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
            { Skins = Skins.RemoveAll(t => t is TSkin).Add(Skin) };
    }

    public TControl WithSkin(Func<TSkin, TSkin> update)
        => This with { Skin = update(Skin) };
}


public abstract record ContainerControlWithItemSkin<TControl,TSkin, TItemSkin>(string ModuleName, string ApiVersion, TSkin Skin)
    : ContainerControl<TControl, TSkin>(ModuleName, ApiVersion, Skin)
    where TControl : ContainerControlWithItemSkin<TControl, TSkin, TItemSkin>
    where TItemSkin: Skin
    where TSkin : Skin
{


    public TControl WithView<T>(T view, Func<TItemSkin, TItemSkin> options)
    => base.WithView(view, x => x with { Skins = Skins.Add(options.Invoke(CreateItemSkin(x))) });




    public TControl WithView<T>(IObservable<T> viewDefinition, Func<TItemSkin, TItemSkin> options)
        => base.WithView(viewDefinition, x => x with { Skins = Skins.Add(options.Invoke(CreateItemSkin(x))) });

    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, T> viewDefinition, Func<TItemSkin, TItemSkin> options)
    => base.WithView(viewDefinition, x => x with { Skins = Skins.Add(options.Invoke(CreateItemSkin(x))) });
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition, Func<TItemSkin, TItemSkin> options)
        => WithView(viewDefinition, x => x with { Skins = Skins.Add(options.Invoke(CreateItemSkin(x))) });



    protected override NamedAreaControl GetNamedArea(Func<NamedAreaControl, NamedAreaControl> options)
    {
        var ret = base.GetNamedArea(options);
        if (!ret.Skins.Any(s => s is TItemSkin))
            ret = ret.AddSkin(CreateItemSkin(ret));
        return ret;
    }

    protected abstract TItemSkin CreateItemSkin(NamedAreaControl ret);
}

