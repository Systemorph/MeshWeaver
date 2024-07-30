using System.Collections.Immutable;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public abstract record ContainerControl<TControl, TItem>(string ModuleName, string ApiVersion, object Data)
    : UiControl<TControl>(ModuleName, ApiVersion, Data), IContainerControl
    where TControl : ContainerControl<TControl, TItem>
    where TItem : UiControl
{
    public TControl WithItems(params TItem[] items)
    {
        return This with
        {
            Items = Items.AddRange(items),
            RawAreas = RawAreas.AddRange(Enumerable.Range(Areas.Count+1,items.Length).Select(x => x.ToString()))
        };
    }

    private ImmutableList<TItem> Items { get; init; } = ImmutableList<TItem>.Empty;

    IContainerControl IContainerControl.SetParentArea(string parentArea)
        => this with { Areas = RawAreas.Select(a => $"{parentArea}/{a}").ToImmutableList() };

    IEnumerable<(string Area, UiControl Control)> IContainerControl.RenderSubAreas(LayoutAreaHost host, RenderingContext context)
        => Items.Select((item, i) => ($"{context.Area}/{i}", (UiControl)item));

    public IReadOnlyCollection<string> Areas { get; init; } = [];
    private ImmutableList<string> RawAreas { get; init; } = ImmutableList<string>.Empty;

}
