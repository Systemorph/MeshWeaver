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
    public LayoutStackControl WithView(Func<LayoutAreaHost, RenderingContext, IObservable<object>> viewDefinition)
        => WithView(GetAutoName(), viewDefinition);
    public LayoutStackControl WithView(string area, Func<LayoutAreaHost, RenderingContext, IObservable<object>> viewDefinition)
        => this with { ViewElements = ViewElements.Add(new ViewElementWithViewStream(area, viewDefinition.Invoke)) };
    public LayoutStackControl WithView(string area, Func<LayoutAreaHost, RenderingContext, object> viewDefinition)
        => WithView(area, (la, ctx) => Observable.Return(viewDefinition.Invoke(la, ctx)));
    public LayoutStackControl WithView(Func<LayoutAreaHost, RenderingContext, object> viewDefinition)
        => WithView(GetAutoName(), viewDefinition);


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
    public static GridSkin Grid() => new();
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

public record SplitterPaneControl()
    : UiControl<SplitterPaneControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public static string ChildContentArea => nameof(ChildContent);
    public UiControl ChildContent { get; init; }
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
    public SplitterPaneControl WithChildContent(UiControl childContent) => this with { ChildContent = childContent };
}

public record GridSkin : Skin
{
    public bool AdaptiveRendering { get; init; }
    public JustifyContent Justify { get; init; }
    public int Spacing { get; init; }

    public GridSkin WithAdaptiveRendering(bool adaptiveRendering) => this with { AdaptiveRendering = adaptiveRendering };
    public GridSkin WithJustify(JustifyContent justify) => this with { Justify = justify };
    public GridSkin WithSpacing(int spacing) => this with { Spacing = spacing };
}

public record LayoutGridItemControl()
    : UiControl<LayoutGridItemControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public static string ChildContentArea => nameof(ChildContent);
    public UiControl ChildContent { get; init; }
    public bool? AdaptiveRendering { get; init; }
    public JustifyContent? Justify { get; init; }
    public LayoutGridItemHidden HiddenWhen { get; init; }
    public string Gap { get; init; }
    public int? Lg { get; init; }
    public int? Md { get; init; }
    public int? Sm { get; init; }
    public int? Xl { get; init; }
    public int? Xs { get; init; }
    public int? Xxl { get; init;}

    public LayoutGridItemControl WithAdaptiveRendering(bool adaptiveRendering) => this with { AdaptiveRendering = adaptiveRendering };
    public LayoutGridItemControl WithGap(string gap) => this with { Gap = gap };
    public LayoutGridItemControl WithLg(int lg) => this with { Lg = lg };
    public LayoutGridItemControl WithMd(int md) => this with { Md = md };
    public LayoutGridItemControl WithSm(int sm) => this with { Sm = sm };
    public LayoutGridItemControl WithXl(int xl) => this with { Xl = xl };
    public LayoutGridItemControl WithXs(int xs) => this with { Xs = xs };
    public LayoutGridItemControl WithXxl(int xxl) => this with { Xxl = xxl };
    public LayoutGridItemControl WithChildContent(UiControl childContent) => this with { ChildContent = childContent };
}

public enum LayoutGridItemHidden
{
    None,
    Xs,
    XsAndDown,
    Sm,
    SmAndDown,
    Md,
    MdAndDown,
    Lg,
    LgAndDown,
    Xl,
    XlAndDown,
    Xxl,
    XxlAndUp,
    XlAndUp,
    LgAndUp,
    MdAndUp,
    SmAndUp,
    XxlAndDown,
    XsAndUp
}
