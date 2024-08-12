using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

public abstract record ContainerControl<TControl, TItem>(string ModuleName, string ApiVersion, object Data)
    : UiControl<TControl>(ModuleName, ApiVersion, Data)
    where TControl : ContainerControl<TControl, TItem>
    where TItem : UiControl
{
    public TControl WithItem(TItem item, Func<NamedAreaControl, NamedAreaControl> options = null)
    {
        options ??= x => x;
        return This with
        {
            Items = Items.Add(item),
            Areas = Areas.Add(options.Invoke(new(null){Id=Items.Count+1}))
        };
    }

    private ImmutableList<TItem> Items { get; init; } = ImmutableList<TItem>.Empty;


    public override IEnumerable<Func<EntityStore, EntityStore>> Render(LayoutAreaHost host,
        RenderingContext context) =>
        base.Render(host, context)
            .Concat(
                Items.Zip(Areas, (item, area) =>
                    (Func<EntityStore, EntityStore>)(store =>
                        store.UpdateControl($"{context.Area}/{area.Id.ToString()}", item))));

    protected override UiControl PrepareRendering(RenderingContext context)
        => this with
            { Areas = Areas.Select(a => a with{Area = $"{context.Area}/{a.Id}"}).ToImmutableList()};
    
    public ImmutableList<NamedAreaControl> Areas { get; init; } = [];
    public virtual bool Equals(ContainerControl<TControl, TItem> other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other))
            return true;

        return base.Equals(other) &&
               Items.SequenceEqual(other.Items) &&
               Areas.SequenceEqual(other.Areas);

    }
    public override int GetHashCode()
    {
        return HashCode.Combine(
            base.GetHashCode(),
            Items.Aggregate(0, (acc, item) => acc ^ item.GetHashCode()),
            Areas.Aggregate(0, (acc, area) => acc ^ area.GetHashCode())
        );
    }
}

