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
        void InitializeHierarchies(IHierarchicalDimensionCache hierarchicalDimensionCache, IHierarchicalDimensionOptions hierarchicalDimensionOptions);
        PivotGroupManager<T, TIntermediate, TAggregate, TGroup> GetPivotGroupManager<TIntermediate, TAggregate>(PivotGroupManager<T, TIntermediate, TAggregate, TGroup> subGroup,
                                                                                                                Aggregations<T, TIntermediate, TAggregate> aggregationFunctions);

        void UpdateMaxLevel(T element);
    }

    public class HierarchyLevelDimensionPivotGrouper<T, TDimension, TGroup> : DimensionPivotGrouper<T, TDimension, TGroup>
        where TGroup : class, IGroup, new()
        where TDimension : class, IHierarchicalDimension
    {
        private readonly int level;
        private readonly IHierarchicalDimensionCache hierarchicalDimensionCache;


        public HierarchyLevelDimensionPivotGrouper(Func<T, int, string> selector, DimensionDescriptor dimensionDescriptor, IDimensionCache dimensionCache, IHierarchicalDimensionCache hierarchicalDimensionCache, int level)
            : base(selector, dimensionDescriptor)
        {
            this.level = level;
            Name += level;
            this.hierarchicalDimensionCache = hierarchicalDimensionCache;
            DimensionCache = dimensionCache;
        }

        // TODO V10: fix order of groups in the group manager (2022/04/21, Ekaterina Mishina)
        public override ICollection<PivotGrouping<TGroup, ICollection<T>>> CreateGroupings(ICollection<T> objects, TGroup nullGroup)
        {
            var selectedObjects = objects.Select((x, i) => new { Key = hierarchicalDimensionCache.Get<TDimension>(Selector(x, i)).AncestorAtLevel(level)?.SystemName, Object = x });

            var grouped = selectedObjects.GroupBy(x => x.Key, x => x.Object);
         
            var ordered = Order(grouped).ToArray();

            // stop parsing if there are no more data on the lower levels
            //if (ordered.Length == 1 && ordered.First().Key == null)
            //    return new List<PivotGrouping<TGroup, ICollection<T>>>();

            var nullGroupPrivate = new TGroup
                                   {
                                       SystemName = nullGroup.SystemName,
                                       DisplayName = nullGroup.DisplayName,
                                       Coordinates = nullGroup.Coordinates,
                                       GrouperName = Name
                                   };

            return ordered
                   .Select(x => new PivotGrouping<TGroup, ICollection<T>>(x.Key == null ? nullGroupPrivate : CreateGroupDefinition(x.Key), x.ToArray(), x.Key))
                   .ToArray();
        }
    }

    public class HierarchicalDimensionPivotGrouper<T, TDimension, TGroup> : DimensionPivotGrouper<T, TDimension, TGroup>, IHierarchicalGrouper<TGroup, T>
        where TGroup : class, IGroup, new()
        where TDimension : class, IHierarchicalDimension
    {
        protected IHierarchicalDimensionCache HierarchicalDimensionCache;
        private int minLevel;
        private int maxLevel;
        private int maxLevelData;
        private bool flat;

        public HierarchicalDimensionPivotGrouper(Func<T, string> selector, DimensionDescriptor dimensionDescriptor)
            : base(selector, dimensionDescriptor)
        {
        }

        public void InitializeHierarchies(IHierarchicalDimensionCache hierarchicalDimensionCache, IHierarchicalDimensionOptions hierarchicalDimensionOptions)
        {
            HierarchicalDimensionCache = hierarchicalDimensionCache;
            if (HierarchicalDimensionCache == null)
            {
                flat = true;
                return;
            }

            HierarchicalDimensionCache.InitializeAsync(DimensionDescriptor);
            minLevel = hierarchicalDimensionOptions.GetLevelMin<TDimension>();
            maxLevel = hierarchicalDimensionOptions.GetLevelMax<TDimension>();
            flat = hierarchicalDimensionOptions.IsFlat<TDimension>();
        }

        public PivotGroupManager<T, TIntermediate, TAggregate, TGroup> GetPivotGroupManager<TIntermediate, TAggregate>(PivotGroupManager<T, TIntermediate, TAggregate, TGroup> subGroup,
                                                                                                                       Aggregations<T, TIntermediate, TAggregate> aggregationFunctions)
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
            maxLevelData = Math.Max(maxLevelData, Math.Min(maxLevel, HierarchicalDimensionCache.Get<TDimension>(dimSystemName).Level()));
        }

        private PivotGroupManager<T, TIntermediate, TAggregate, TGroup> AddChildren<TIntermediate, TAggregate>(int level,
                                                                                                               PivotGroupManager<T, TIntermediate, TAggregate, TGroup> subGroup,
                                                                                                               Aggregations<T, TIntermediate, TAggregate> aggregationFunctions)
        {
            var grouper = new HierarchyLevelDimensionPivotGrouper<T, TDimension, TGroup>(Selector, DimensionDescriptor, DimensionCache, HierarchicalDimensionCache, level);

            return new(grouper, subGroup, aggregationFunctions);
        }
    }
}