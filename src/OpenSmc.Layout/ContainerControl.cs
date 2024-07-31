using System.Collections.Immutable;
using System.Runtime.InteropServices.ComTypes;
using OpenSmc.Data;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public abstract record ContainerControl<TControl, TItem>(string ModuleName, string ApiVersion, object Data)
    : UiControl<TControl>(ModuleName, ApiVersion, Data)
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


    public override IEnumerable<Func<EntityStore, EntityStore>> Render(LayoutAreaHost host,
        RenderingContext context) =>
        base.Render(host, context)
            .Concat(
                Items.Select((item, i) =>
                    (Func<EntityStore, EntityStore>)(store =>
                        store.UpdateControl($"{context.Area}/{i}", (UiControl)item))));
    protected override Func<EntityStore, EntityStore> RenderSelf(LayoutAreaHost host, RenderingContext context)
        => store => store.UpdateControl(context.Area, this with 
            {Areas = Enumerable.Range(0,Items.Count).Select(i => $"{context.Area}/{i}").ToArray() });

    public IReadOnlyCollection<string> Areas { get; init; } = [];
    private ImmutableList<string> RawAreas { get; init; } = ImmutableList<string>.Empty;

}

