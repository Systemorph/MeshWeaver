using System.Collections.Immutable;
using System.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Domain;
using MeshWeaver.Hierarchies;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Grouping;

public class DimensionPivotGrouper<T, TDimension, TGroup>
    (string id, Func<T?, int, object?> selector, DimensionCache dimensionCache) :
    SelectorPivotGrouper<T, object, TGroup>(id, selector)
    where TGroup : class, IGroup, new()
    where TDimension : class, INamed
{
    protected DimensionCache DimensionCache { get; } = dimensionCache;
    public DimensionPivotGrouper(
        DimensionDescriptor dimensionDescriptor,
        Func<T?, object?> selector,
        DimensionCache dimensionCache
    )
        : this(dimensionDescriptor.SystemName, (x, _) => selector(x), dimensionCache)
    {
        this.DimensionDescriptor = dimensionDescriptor;
    }

    public DimensionDescriptor DimensionDescriptor { get; } = null!;

    protected override IOrderedEnumerable<IGrouping<object?, T?>> Order(
        IEnumerable<IGrouping<object?, T?>> groups
    )
    {
        if (!typeof(IOrdered).IsAssignableFrom(typeof(TDimension)))
            return groups.OrderBy(g => g.Key == null).ThenBy(g => GetDisplayName(g.Key));

        return groups.OrderBy(g => g.Key == null).ThenBy(g => GetOrder(g.Key));
    }


    public override IEnumerable<TGroup?> Order(IEnumerable<IdentityWithOrderKey<TGroup>> groups)
    {
        if (typeof(IOrdered).IsAssignableFrom(typeof(TDimension)))
            return groups
                .OrderBy(x => x.OrderKey == null)
                .ThenBy(g => GetOrder(g.OrderKey))
                .Select(x => x.Identity);
        return groups
            .OrderBy(x => x.OrderKey == null)
            .ThenBy(g => GetDisplayName(g.OrderKey))
            .Select(x => x.Identity);
    }

    protected override TGroup CreateGroupDefinition(object value)
    {
        if (value == null)
            throw new NoNullAllowedException();

        var displayName = GetDisplayName(value);
        return new TGroup
        {
            Id = value,
            DisplayName = displayName.ToString() ?? value.ToString() ?? "",
            GrouperName = Id,
            Coordinates = ImmutableList<object>.Empty.Add(value)
        };
    }
    private int GetOrder(object? dim)
    {
        if (dim == null)
            return int.MaxValue;
        var ordered = DimensionCache.Get<TDimension>(dim) as IOrdered;
        return ordered?.Order ?? int.MaxValue;
    }

    private object GetDisplayName(object? id)
    {
        if (id == null)
            return IPivotGrouper<T, TGroup>.NullGroup.DisplayName;
        return DimensionCache.Get<TDimension>(id)?.DisplayName ?? id;
    }
}
