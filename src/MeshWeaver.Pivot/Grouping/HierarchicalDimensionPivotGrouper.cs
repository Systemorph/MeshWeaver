﻿using MeshWeaver.Data;
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

        void UpdateMaxLevel(T element);
    }

    public class HierarchyLevelDimensionPivotGrouper<T, TDimension, TGroup>(
        string name,
        int level,
        EntityStore store,
        Func<T, int, object> selector
    ) : DimensionPivotGrouper<T, TDimension, TGroup>(store, selector, name + level)
        where TGroup : class, IGroup, new()
        where TDimension : class, IHierarchicalDimension
    {
        private readonly HierarchicalDimensionCache hierarchicalDimensionCache = new(store);

        // TODO V10: fix order of groups in the group manager (2022/04/21, Ekaterina Mishina)
        public override IReadOnlyCollection<
            PivotGrouping<TGroup, IReadOnlyCollection<T>>
        > CreateGroupings(IReadOnlyCollection<T> objects, TGroup nullGroup)
        {
            var selectedObjects = objects.Select(
                (x, i) =>
                    new
                    {
                        Key = hierarchicalDimensionCache.AncestorIdAtLevel<TDimension>(
                            Selector(x, i),
                            level
                        ),
                        Object = x
                    }
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
                GrouperName = Id
            };

            return ordered
                .Select(x => new PivotGrouping<TGroup, IReadOnlyCollection<T>>(
                    x.Key == null ? nullGroupPrivate : CreateGroupDefinition(x.Key),
                    x.ToArray(),
                    x.Key
                ))
                .ToArray();
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
            EntityStore state,
            IHierarchicalDimensionCache hierarchicalDimensionCache,
            IHierarchicalDimensionOptions hierarchicalDimensionOptions,
            Func<T, object> selector,
            DimensionDescriptor dimensionDescriptor
        )
            : base(state, selector, dimensionDescriptor)
        {
            InitializeHierarchies(hierarchicalDimensionCache, hierarchicalDimensionOptions);
        }

        public void InitializeHierarchies(
            IHierarchicalDimensionCache hierarchicalDimensionCache,
            IHierarchicalDimensionOptions hierarchicalDimensionOptions
        )
        {
            HierarchicalDimensionCache = hierarchicalDimensionCache;
            if (
                HierarchicalDimensionCache == null
                || !hierarchicalDimensionCache.Has(typeof(TDimension))
            )
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
            var hierarchyNode = HierarchicalDimensionCache.Get<TDimension>(dimSystemName);
            if (hierarchyNode == null)
                return;
            maxLevelData = Math.Max(maxLevelData, Math.Min(maxLevel, hierarchyNode.Level));
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
                Store,
                Selector
            );

            return new(grouper, subGroup, aggregationFunctions);
        }
    }
}
