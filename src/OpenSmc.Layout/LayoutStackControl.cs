using System.Collections.Immutable;
using System.Reactive.Linq;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public record LayoutStackControl(): UiControl<LayoutStackControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null), IContainerControl
{
    internal const string Root = "";
    private ImmutableList<Renderer> Renderers { get; init; } = ImmutableList<Renderer>.Empty;
    private string GetAutoName() => $"{Renderers.Count + 1}";
    public LayoutStackControl WithView(object value) => WithView(GetAutoName(), value);

    public LayoutStackControl WithView(string area, object view) =>
        this with
        {
            RawAreas = RawAreas.Add(area),
            Renderers = Renderers.Add((host,context,_) => host.RenderArea(GetContextForArea(context, area), view))
        };

    private static RenderingContext GetContextForArea(RenderingContext context, string area)
    {
        return context with{Area = $"{context.Area}/{area}", Parent=context};
    }

    public LayoutStackControl WithView<T>(ViewDefinition<T> viewDefinition) =>
        WithView(GetAutoName(), Observable.Return(viewDefinition));


    public LayoutStackControl WithView<T>(string area, IObservable<ViewDefinition<T>> viewDefinition) =>
        this with
        {
            RawAreas = RawAreas.Add(area),
            Renderers = Renderers.Add((host,context,_) => host.RenderArea(GetContextForArea(context, area), viewDefinition))
        };

    public LayoutStackControl WithView(string area, IObservable<ViewDefinition> viewDefinition) =>
        this with
        {
            RawAreas = RawAreas.Add(area),
            Renderers = Renderers.Add((host, context, _) => host.RenderArea(GetContextForArea(context, area), viewDefinition))
        };
    public LayoutStackControl WithView(IObservable<object> viewDefinition) =>
        WithView(GetAutoName(), viewDefinition);
    public LayoutStackControl WithView(string area, IObservable<object> viewDefinition) =>
        this with
        {
            RawAreas = RawAreas.Add(area),
            Renderers = Renderers.Add((host, context, _) => host.RenderArea(GetContextForArea(context, area), viewDefinition))
        };

    public LayoutStackControl WithView(IObservable<ViewDefinition> viewDefinition)
        => WithView(GetAutoName(), viewDefinition);

    public LayoutStackControl WithView<T>(ViewStream<T> viewDefinition)
        => WithView(GetAutoName(), viewDefinition);
    public LayoutStackControl WithView<T>(string area, ViewStream<T> viewDefinition)
        => this with
        {
            RawAreas = RawAreas.Add(area),
            Renderers = Renderers.Add((host, context, _) => host.RenderArea(GetContextForArea(context, area),viewDefinition.Invoke))
        };


    public LayoutStackControl WithView(string area, Func<LayoutAreaHost, RenderingContext, object> viewDefinition)
        => WithView(area, (la, ctx) => Observable.Return(viewDefinition.Invoke(la, ctx)));
    public LayoutStackControl WithView(Func<LayoutAreaHost, RenderingContext, object> viewDefinition)
        => WithView(GetAutoName(), viewDefinition);


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

    public IReadOnlyCollection<string> Areas { get; init; } = [];
    private ImmutableList<string> RawAreas { get; init; } = ImmutableList<string>.Empty;

    IEnumerable<(string Area, UiControl Control)> IContainerControl.RenderSubAreas(LayoutAreaHost host, RenderingContext context)
        => 
            Renderers.SelectMany(r => r.Invoke(host, context, null));

    IContainerControl IContainerControl.SetParentArea(string parentArea)
        => this with { Areas = RawAreas.Select(a => $"{parentArea}/{a}").ToImmutableList() };

}
