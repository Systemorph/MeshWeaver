using System.Data;
using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.Domain.Abstractions;
using OpenSmc.Hierarchies;
using OpenSmc.Pivot.Aggregations;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Grouping
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

        void UpdateMaxLevel(T element);
    }

    public class HierarchyLevelDimensionPivotGrouper<T, TDimension, TGroup>(
        WorkspaceState state,
        Func<T, int, object> selector,
        DimensionDescriptor dimensionDescriptor,
        IHierarchicalDimensionCache hierarchicalDimensionCache,
        int level
    ) : DimensionPivotGrouper<T, TDimension, TGroup>(state, selector, dimensionDescriptor)
        where TGroup : class, IGroup, new()
        where TDimension : class, IHierarchicalDimension
    {
        // TODO V10: fix order of groups in the group manager (2022/04/21, Ekaterina Mishina)
        public override IReadOnlyCollection<
            PivotGrouping<TGroup, IReadOnlyCollection<T>>
        > CreateGroupings(IReadOnlyCollection<T> objects, TGroup nullGroup)
        {
            var selectedObjects = objects.Select(
                (x, i) => new { Key = GetAncestor(x, i), Object = x }
            );

            var grouped = selectedObjects.GroupBy(x => x.Key, x => x.Object);

            var ordered = Order(grouped).ToArray();

            // stop parsing if there are no more data on the lower levels
            //if (ordered.Length == 1 && ordered.First().Key == null)
            //    return new List<PivotGrouping<TGroup, ICollection<T>>>();

            var nullGroupPrivate = new TGroup
            {
                Id = nullGroup.Id,
                DisplayName = nullGroup.DisplayName,
                Coordinates = nullGroup.Coordinates,
                GrouperId = Id
            };

            return ordered
                .Select(x => new PivotGrouping<TGroup, IReadOnlyCollection<T>>(
                    x.Key == null ? nullGroupPrivate : CreateGroupDefinition(x.Key),
                    x.ToArray(),
                    x.Key
                ))
                .ToArray();
        }

        private object GetAncestor(T element, int i)
        {
            var el = Selector(element, i);
            var hierarchy = hierarchicalDimensionCache.Get<TDimension>(el);

            while (hierarchy.Level > level)
                hierarchy = hierarchicalDimensionCache.Get<TDimension>(hierarchy.ParentId);

            return hierarchy.Element;
        }
    }

    public class HierarchicalDimensionPivotGrouper<T, TDimension, TGroup>
        : DimensionPivotGrouper<T, TDimension, TGroup>,
            IHierarchicalGrouper<TGroup, T>
        where TGroup : class, IGroup, new()
        where TDimension : class, IHierarchicalDimension
    {
        protected IHierarchicalDimensionCache HierarchicalDimensionCache;
        private int minLevel;
        private int maxLevel;
        private int maxLevelData;
        private bool flat;

        public HierarchicalDimensionPivotGrouper(
            WorkspaceState state,
            Func<T, object> selector,
            DimensionDescriptor dimensionDescriptor
        )
            : base(state, selector, dimensionDescriptor) { }

        public void InitializeHierarchies(
            IHierarchicalDimensionCache hierarchicalDimensionCache,
            IHierarchicalDimensionOptions hierarchicalDimensionOptions
        )
        {
            HierarchicalDimensionCache = hierarchicalDimensionCache;
            if (HierarchicalDimensionCache == null)
            {
                flat = true;
                return;
            }

            minLevel = hierarchicalDimensionOptions.GetLevelMin<TDimension>();
            maxLevel = hierarchicalDimensionOptions.GetLevelMax<TDimension>();
            flat = hierarchicalDimensionOptions.IsFlat<TDimension>();
        }

        public PivotGroupManager<T, TIntermediate, TAggregate, TGroup> GetPivotGroupManager<
            TIntermediate,
            TAggregate
        >(
            PivotGroupManager<T, TIntermediate, TAggregate, TGroup> subGroup,
            Aggregations<T, TIntermediate, TAggregate> aggregationFunctions
        )
        {
            if (flat)
                return new(this, subGroup, aggregationFunctions);

            var groupManager = subGroup;
            for (var i = maxLevelData; i >= minLevel; i--)
            {
                groupManager = AddChildren(i, groupManager, aggregationFunctions);
            }
            return groupManager;
        }

        public void UpdateMaxLevel(T element)
        {
            if (flat)
                return;
            var dimSystemName = Selector(element, 0);
            maxLevelData = Math.Max(
                maxLevelData,
                Math.Min(maxLevel, HierarchicalDimensionCache.Get<TDimension>(dimSystemName).Level)
            );
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
                State,
                Selector,
                DimensionDescriptor,
                HierarchicalDimensionCache,
                level
            );

            return new(grouper, subGroup, aggregationFunctions);
        }
    }
}
