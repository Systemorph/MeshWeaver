using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

public record LayoutStackControl(): UiControl<LayoutStackControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    internal const string Root = "";
    private ImmutableList<Renderer> Renderers { get; init; } = ImmutableList<Renderer>.Empty;
    private string GetAutoName() => $"{Renderers.Count + 1}";
    public LayoutStackControl WithView(object value) => WithView(value, x => x);

    public LayoutStackControl WithView(object view, Func<NamedAreaControl,NamedAreaControl> options)
    {
        var area = Evaluate(options);
        return this with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context) => host.RenderArea(GetContextForArea(context, area.Id.ToString()), view))
        };
    }

    public LayoutStackControl WithView(object view, string area) =>
        WithView(view, control => control.WithId(area));
    public override IEnumerable<Func<EntityStore, EntityStore>> Render(LayoutAreaHost host, RenderingContext context) =>
        base.Render(host, context)
            .Concat(Renderers.SelectMany(r => (r.Invoke(host, context))));
    protected override Func<EntityStore, EntityStore> RenderSelf(LayoutAreaHost host, RenderingContext context)
        => store => store.UpdateControl(context.Area, this with
            { Areas = Areas.Select(a => a with{Area = $"{context.Area}/{a.Id}"}).ToImmutableList() });

    private static RenderingContext GetContextForArea(RenderingContext context, string area)
    {
        return context with{Area = $"{context.Area}/{area}", Parent=context};
    }
    

    public LayoutStackControl WithView<T>(ViewDefinition<T> viewDefinition) =>
        WithView(Observable.Return(viewDefinition), x => x);


    public LayoutStackControl WithView<T>(IObservable<ViewDefinition<T>> viewDefinition, Func<NamedAreaControl,NamedAreaControl> options)
    {
        var area = Evaluate(options);
        return this with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context) =>
                host.RenderArea(GetContextForArea(context, area.Id.ToString()), viewDefinition))
        };
    }

    private NamedAreaControl Evaluate(Func<NamedAreaControl, NamedAreaControl> area)
    {
        return area.Invoke(new(null){Id = GetAutoName() });
    }

    public LayoutStackControl WithView(IObservable<ViewDefinition> viewDefinition, Func<NamedAreaControl,NamedAreaControl> options)
    {
        var area = Evaluate(options);
        return this with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context) =>
                host.RenderArea(GetContextForArea(context, area.Id.ToString()), viewDefinition))
        };
    }

    public LayoutStackControl WithView(IObservable<ViewDefinition> viewDefinition, string area) =>
        WithView(viewDefinition, control => control.WithId(area));
    public LayoutStackControl WithView(IObservable<object> viewDefinition) =>
        WithView(viewDefinition, x => x);
    public LayoutStackControl WithView(IObservable<object> viewDefinition, Func<NamedAreaControl,NamedAreaControl> options)
    {
        var area = Evaluate(options);
        return this with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context) =>
                host.RenderArea(GetContextForArea(context, area.Id.ToString()), viewDefinition))
        };
    }

    public LayoutStackControl WithView(IObservable<object> viewDefinition, string area) =>
        WithView(viewDefinition, control => control.WithId(area));
    public LayoutStackControl WithView(IObservable<ViewDefinition> viewDefinition)
        => WithView(viewDefinition, x => x);

    public LayoutStackControl WithView<T>(ViewStream<T> viewDefinition)
        => WithView(viewDefinition, x => x);
    public LayoutStackControl WithView<T>(ViewStream<T> viewDefinition, Func<NamedAreaControl,NamedAreaControl> options)
    {
        var area = Evaluate(options);
        
        return this with
        {
            Areas = Areas.Add(area),
            Renderers = Renderers.Add((host, context) =>
                host.RenderArea(GetContextForArea(context, area.Id.ToString()), viewDefinition.Invoke))
        };
    }

    public LayoutStackControl WithView<T>(ViewStream<T> viewDefinition, string area)
        => WithView(viewDefinition, control => control.WithId(area));

    public LayoutStackControl WithView(Func<LayoutAreaHost, RenderingContext, object> viewDefinition, Func<NamedAreaControl,NamedAreaControl> options)
        => WithView((la, ctx) => Observable.Return(viewDefinition.Invoke(la, ctx)), options);
    public LayoutStackControl WithView(Func<LayoutAreaHost, RenderingContext, object> viewDefinition, string area)
    => WithView(viewDefinition, control => control.WithId(area));
    public LayoutStackControl WithView(Func<LayoutAreaHost, RenderingContext, object> viewDefinition)
        => WithView(viewDefinition, x => x);


    public HorizontalAlignment HorizontalAlignment { get; init; }

    public VerticalAlignment VerticalAlignment { get; init; }
    public int? HorizontalGap { get; init; }
    public int? VerticalGap { get; init; }
    public Orientation? Orientation { get; init; }
    public bool Wrap { get; init; }
    public string Width { get; init; }
    public string Height { get; init; }


    public LayoutStackControl WithHorizontalAlignment(HorizontalAlignment horizontalAlignment)
        => this with { HorizontalAlignment = horizontalAlignment };
    public LayoutStackControl WithVerticalAlignment(VerticalAlignment verticalAlignment)
    => this with { VerticalAlignment = verticalAlignment };
    public LayoutStackControl WithHorizontalGap(int? horizontalGap)
        => this with { HorizontalGap = horizontalGap };
    public LayoutStackControl WithVerticalGap(int? verticalGap)
        => this with { VerticalGap = verticalGap };
    public LayoutStackControl WithOrientation(Orientation orientation)
    => this with { Orientation = orientation };
    public LayoutStackControl WithWrap(bool wrap)
        => this with { Wrap = wrap };
    public LayoutStackControl WithWidth(string width)
    => this with { Width = width };
    public LayoutStackControl WithHeight(string height) => this with { Height = height };

    public ImmutableList<NamedAreaControl> Areas { get; init; } = ImmutableList<NamedAreaControl>.Empty;

    public virtual bool Equals(LayoutStackControl other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other))
            return true;
        return base.Equals(other) &&
               HorizontalAlignment == other.HorizontalAlignment &&
               VerticalAlignment == other.VerticalAlignment &&
               HorizontalGap == other.HorizontalGap &&
               VerticalGap == other.VerticalGap &&
               Orientation == other.Orientation &&
               Wrap == other.Wrap &&
               Width == other.Width &&
               Height == other.Height &&
               Areas.SequenceEqual(other.Areas) &&
               Areas.SequenceEqual(other.Areas);

    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            base.GetHashCode(),
            HashCode.Combine(
                Renderers.Aggregate(0, (acc, renderer) => acc ^ renderer.GetHashCode()),
                HorizontalAlignment.GetHashCode(),
                VerticalAlignment.GetHashCode(),
                HorizontalGap?.GetHashCode(),
                VerticalGap?.GetHashCode(),
                Orientation?.GetHashCode(),
                Wrap.GetHashCode()
            ),
            HashCode.Combine(
                Areas.Aggregate(0, (acc, area) => acc ^ area.GetHashCode()),
                Areas.Aggregate(0, (acc, rawArea) => acc ^ rawArea.GetHashCode()),
                Width?.GetHashCode() ?? 0,
                Height?.GetHashCode() ?? 0
            )
        );
    }
}

public static class StackSkins
{
    public static LayoutSkin Layout => new();
    public static LayoutGridSkin LayoutGrid => new();
    public static SplitterSkin Splitter => new();
}
