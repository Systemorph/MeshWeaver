using System.Collections.Immutable;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public abstract record ContainerControl<TControl, TItem>(string ModuleName, string ApiVersion, object Data)
    : UiControl<TControl>(ModuleName, ApiVersion, Data), IContainerControl
    where TControl : ContainerControl<TControl, TItem>
    where TItem : UiControl
{
    protected TControl This => (TControl)this;
    public TControl WithItems(params TItem[] items)
    {
        return This with { Items = Items.AddRange(items) };
    }

    internal ImmutableList<TItem> Items { get; init; } = ImmutableList<TItem>.Empty;

    IContainerControl IContainerControl.SetParentArea(string parentArea)
        => this with { Areas = Areas.Select(a => $"{parentArea}/{a}").ToImmutableList() };

    IEnumerable<(string Area, UiControl Control)> IContainerControl.RenderSubAreas(LayoutAreaHost host, RenderingContext context)
        => Items.Select((item, i) => ($"{context.Area}/{i}", (UiControl)item));

    public IReadOnlyCollection<string> Areas { get; init; }

}
