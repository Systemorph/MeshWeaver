using MeshWeaver.DataCubes;
using MeshWeaver.Domain;
using MeshWeaver.Hierarchies;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Grouping
{
    public interface IHierarchicalGrouper<TGroup, T>
        where TGroup : class, IGroup, new()
    {
        PivotGroupManager<T, TIntermediate, TAggregate, TGroup> GetPivotGroupManager<
            TIntermediate,
            TAggregate
        >(
            PivotGroupManager<T, TIntermediate, TAggregate, TGroup> subGroup,
            Aggregations<T, TIntermediate, TAggregate> aggregationFunctions
        );

    }
    public class HierarchyLevelDimensionPivotGrouper<T, TDimension, TGroup>(
        string name,
        int level,
        DimensionCache dimensionCache,
        Func<T, int, object> selector
    ) : DimensionPivotGrouper<T, TDimension, TGroup>(name + level, selector, dimensionCache)
        where TGroup : class, IGroup, new()
        where TDimension : class, IHierarchicalDimension
    {
        private readonly DimensionCache dimensionCache = dimensionCache;

        // TODO V10: fix order of groups in the group manager (2022/04/21, Ekaterina Mishina)
        public override IReadOnlyCollection<PivotGrouping<TGroup, IReadOnlyCollection<T>>> CreateGroupings(
            IReadOnlyCollection<T> objects, TGroup nullGroup)
        {
            var selectedObjects = objects.Select(
                (x, i) =>
                    new
                    {
                        Key = dimensionCache.AncestorIdAtLevel<TDimension>(
                            Selector(x, i),
                            level
                        ),
                        Object = x
                    }
            );

            var grouped = selectedObjects.GroupBy(x => x.Key, x => x.Object);

            var orderedNonNull = grouped.Where(g => g.Key != null).Cast<IGrouping<object, T>>();
            var ordered = Order(orderedNonNull).ToArray();

            // stop parsing if there are no more data on the lower levels
            //if (ordered.Length == 1 && ordered.First().Key == null)
            //    return new List<PivotGrouping<TGroup, ICollection<T>>>();

            var nullGroupPrivate = new TGroup
            {
                Id = nullGroup.Id,
                DisplayName = nullGroup.DisplayName,
                Coordinates = nullGroup.Coordinates,
                GrouperName = Id
            };

            return ordered
                .Select(x => new PivotGrouping<TGroup, IReadOnlyCollection<T>>(
                    x.Key == null ? nullGroupPrivate : CreateGroupDefinition(x.Key),
                    x.ToArray(),
                    x.Key ?? nullGroupPrivate
                ))
                .ToArray();
        }
    }

    public class HierarchicalDimensionPivotGrouper<T, TDimension, TGroup>(
        DimensionCache dimensionCache,
        IHierarchicalDimensionOptions hierarchicalDimensionOptions,
        Func<T, object> selector,
        DimensionDescriptor dimensionDescriptor)
        : DimensionPivotGrouper<T, TDimension, TGroup>(dimensionDescriptor, selector, dimensionCache),
            IHierarchicalGrouper<TGroup, T>
        where TGroup : class, IGroup, new()
        where TDimension : class, IHierarchicalDimension
    {
        private IHierarchicalDimensionOptions DimensionOptions { get; } = hierarchicalDimensionOptions;

        private bool Flat => DimensionCache == null
                             || !DimensionCache.Has(typeof(TDimension))
                             || DimensionOptions.IsFlat<TDimension>();

        public PivotGroupManager<T, TIntermediate, TAggregate, TGroup> GetPivotGroupManager<
            TIntermediate,
            TAggregate
        >(
            PivotGroupManager<T, TIntermediate, TAggregate, TGroup> subGroup,
            Aggregations<T, TIntermediate, TAggregate> aggregationFunctions
        )
        {
            if (Flat)
                return new(this, subGroup, aggregationFunctions);

            var groupManager = subGroup;
            var maxLevel = Math.Min(DimensionOptions.GetLevelMax<TDimension>(), DimensionCache.GetMaxHierarchyDataLevel(typeof(TDimension)));
            var minLevel = DimensionOptions.GetLevelMin<TDimension>();
            for (var i = maxLevel; i >= minLevel; i--)
            {
                groupManager = AddChildren(i, groupManager, aggregationFunctions);
            }
            return groupManager;
        }


        private PivotGroupManager<T, TIntermediate, TAggregate, TGroup> AddChildren<
            TIntermediate,
            TAggregate
        >(
            int level,
            PivotGroupManager<T, TIntermediate, TAggregate, TGroup> subGroup,
            Aggregations<T, TIntermediate, TAggregate> aggregationFunctions
        )
        {
            var grouper = new HierarchyLevelDimensionPivotGrouper<T, TDimension, TGroup>(
                DimensionDescriptor.SystemName,
                level,
                DimensionCache,
                Selector
            );

            return new(grouper, subGroup, aggregationFunctions);
        }
    }
}
