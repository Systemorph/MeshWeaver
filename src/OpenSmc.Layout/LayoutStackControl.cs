using System.Collections.Immutable;
using System.Reactive.Linq;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public interface IContainerControl : IUiControl
{
    IEnumerable<ViewElement> SubAreas { get; }
    IReadOnlyCollection<string> Areas { get; }
    IContainerControl SetAreas(IReadOnlyCollection<string> areas);
}

public record LayoutStackControl()
    : UiControl<LayoutStackControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null),
        IContainerControl
{
    internal const string Root = "";

    internal ImmutableList<ViewElement> ViewElements { get; init; } =
        ImmutableList<ViewElement>.Empty;

    public IReadOnlyCollection<string> Areas { get; init; }

    public LayoutStackControl WithView(object value) => WithView(GetAutoName(), value, x => x);
    public LayoutStackControl WithView(object value, Func<LayoutAreaProperties, LayoutAreaProperties> options) => WithView(GetAutoName(), value, options);

    public LayoutStackControl WithView(string area, object value) =>
    WithView(area, value, x => x);
    public LayoutStackControl WithView(string area, object value, Func<LayoutAreaProperties, LayoutAreaProperties> options) =>
        this with
        {
            ViewElements = ViewElements.Add(new ViewElementWithView(area, value, options.Invoke(new())))
        };

    private string GetAutoName()
    {
        return $"Area{ViewElements.Count + 1}";
    }

    public LayoutStackControl WithView(ViewDefinition viewDefinition) =>
        WithView(viewDefinition, x => x);
    public LayoutStackControl WithView(ViewDefinition viewDefinition, Func<LayoutAreaProperties, LayoutAreaProperties> options) =>
        WithView(GetAutoName(), Observable.Return(viewDefinition), options);

    public LayoutStackControl WithView(string area, IObservable<ViewDefinition> viewDefinition) =>
        WithView(area, viewDefinition, x => x);
    public LayoutStackControl WithView(string area, IObservable<ViewDefinition> viewDefinition, Func<LayoutAreaProperties, LayoutAreaProperties> options) =>
        this with
        {
            ViewElements = ViewElements.Add(new ViewElementWithViewDefinition(area, viewDefinition, options.Invoke(new())))
        };

    public LayoutStackControl WithView(IObservable<ViewDefinition> viewDefinition)
        => WithView(GetAutoName(), viewDefinition, x=>x);
    public LayoutStackControl WithView(IObservable<ViewDefinition> viewDefinition, Func<LayoutAreaProperties, LayoutAreaProperties> options)
        => WithView(GetAutoName(), viewDefinition, options);

    public LayoutStackControl WithView<T>(Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition)
        => WithView(viewDefinition, x => x);
    public LayoutStackControl WithView<T>(Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition, Func<LayoutAreaProperties, LayoutAreaProperties> options)
        => WithView(GetAutoName(), viewDefinition, options);
    public LayoutStackControl WithView<T>(string area, Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition)
    => WithView(area, viewDefinition, x => x);
    public LayoutStackControl WithView<T>(string area, Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition, Func<LayoutAreaProperties, LayoutAreaProperties> options)
        => this with { ViewElements = ViewElements.Add(new ViewElementWithViewStream(area, (a,c) => (viewDefinition.Invoke(a,c)?.Select(x => (object)x)), options.Invoke(new()))) };

    public LayoutStackControl WithView(string area, Func<LayoutAreaHost, RenderingContext, object> viewDefinition)
        => WithView(area, viewDefinition, x => x);
    
    public LayoutStackControl WithView(string area, Func<LayoutAreaHost, RenderingContext, object> viewDefinition, Func<LayoutAreaProperties, LayoutAreaProperties> options)
        => WithView(area, (la, ctx) => Observable.Return(viewDefinition.Invoke(la, ctx)), options);
    public LayoutStackControl WithView(Func<LayoutAreaHost, RenderingContext, object> viewDefinition)
    => WithView(viewDefinition, x => x);
    public LayoutStackControl WithView(Func<LayoutAreaHost, RenderingContext, object> viewDefinition, Func<LayoutAreaProperties, LayoutAreaProperties> options)
        => WithView(GetAutoName(), viewDefinition, options);


    public HorizontalAlignment HorizontalAlignment { get; init; }

    public VerticalAlignment VerticalAlignment { get; init; }
    public int? HorizontalGap { get; init; }
    public int? VerticalGap { get; init; }
    public Orientation? Orientation { get; init; }
    public bool Wrap { get; init; }
    public string Width { get; init; }

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

    IEnumerable<ViewElement> IContainerControl.SubAreas => ViewElements;
    IContainerControl IContainerControl.SetAreas(IReadOnlyCollection<string> areas) => this with { Areas = areas };
}
