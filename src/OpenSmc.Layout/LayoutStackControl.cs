using System.Collections.Immutable;
using System.Reactive.Linq;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public record LayoutStackControl()
    : UiControl<LayoutStackControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    internal const string Root = "";

    internal ImmutableList<ViewElement> ViewElements { get; init; } =
        ImmutableList<ViewElement>.Empty;

    public IReadOnlyCollection<string> Areas { get; init; }

    public LayoutStackControl WithView(object value) => WithView(GetAutoName(), value);

    public LayoutStackControl WithView(string area, object value) =>
        this with
        {
            ViewElements = ViewElements.Add(new ViewElementWithView(area, value))
        };

    private string GetAutoName()
    {
        return $"Area{ViewElements.Count + 1}";
    }

    public LayoutStackControl WithView(ViewDefinition viewDefinition) =>
        WithView(GetAutoName(), Observable.Return(viewDefinition));

    public LayoutStackControl WithView(string area, IObservable<ViewDefinition> viewDefinition) =>
        this with
        {
            ViewElements = ViewElements.Add(new ViewElementWithViewDefinition(area, viewDefinition))
        };

    public LayoutStackControl WithView(IObservable<ViewDefinition> viewDefinition)
        => WithView(GetAutoName(), viewDefinition);
    public LayoutStackControl WithView(Func<LayoutArea, IObservable<object>> viewDefinition)
        => WithView(GetAutoName(), viewDefinition);
    public LayoutStackControl WithView(string area, Func<LayoutArea, IObservable<object>> viewDefinition)
        => this with { ViewElements = ViewElements.Add(new ViewElementWithViewStream(area, viewDefinition.Invoke)) };
    public LayoutStackControl WithView(string area, Func<LayoutArea, object> viewDefinition)
        => WithView(area, la => Observable.Return(viewDefinition.Invoke(la)));


    public HorizontalAlignment HorizontalAlignment { get; init; }

    public VerticalAlignment VerticalAlignment { get; init; }
    public int? HorizontalGap { get; init; }
    public int? VerticalGap { get; init; }
    public Orientation Orientation { get; init; }
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
}


public static class Skins
{
    public static ToolbarSkin Toolbar() => new();
    public static SplitterSkin Splitter() => new();
}

public record ToolbarSkin : Skin;

public record SplitterSkin : Skin
{
    public string BarSize { get; init; }
    public string Width { get; init; }
    public string Height { get; init; }

    public SplitterSkin WithBarSize(string barSize) => this with { BarSize = barSize };

    public SplitterSkin WithWidth(string width) => this with { Width = width };

    public SplitterSkin WithHeight(string height) => this with { Height = height };
}

public record SplitterPaneControl(UiControl Content)
    : UiControl<SplitterPaneControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public bool Collapsible { get; init; }
    public bool Collapsed { get; init; }

    public string Max { get; init; }
    public string Min { get; init; }
    public bool Resizable { get; init; } = true;

    public string Size { get; init; }

    public SplitterPaneControl WithCollapsible(bool collapsible) => this with { Collapsible = collapsible };
    public SplitterPaneControl WithCollapsed(bool collapsed) => this with { Collapsed = collapsed };
    public SplitterPaneControl WithMax(string max) => this with { Max = max };
    public SplitterPaneControl WithMin(string min) => this with { Min = min };
    public SplitterPaneControl WithResizable(bool resizable) => this with { Resizable = resizable };
    public SplitterPaneControl WithSize(string size) => this with { Size = size };
}
