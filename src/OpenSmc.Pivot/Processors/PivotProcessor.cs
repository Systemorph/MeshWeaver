using OpenSmc.Hierarchies;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Grouping;
using OpenSmc.Pivot.Models;

namespace OpenSmc.Pivot.Processors
{
    public class PivotProcessor<T, TIntermediate, TAggregate> : PivotProcessorBase<T, T, TIntermediate, TAggregate,
                                                                                                PivotBuilder<T, TIntermediate, TAggregate>>
    {
        public PivotProcessor(IPivotConfiguration<TAggregate, ColumnGroup> colConfig,
                              IPivotConfiguration<TAggregate, RowGroup> rowConfig,
                              PivotBuilder<T, TIntermediate, TAggregate> pivotBuilder)
            : base(pivotBuilder)
        {
            ColumnConfig = colConfig;
            RowConfig = rowConfig;
        }

        protected override PivotGroupManager<T, TIntermediate, TAggregate, ColumnGroup> GetColumnGroupManager()
        {
            PivotGroupManager<T, TIntermediate, TAggregate, ColumnGroup> columnGroupManager = null;

            foreach (var groupConfig in PivotBuilder.ColumnGroupConfig)
            {
                columnGroupManager = groupConfig.GetGroupManager(columnGroupManager, PivotBuilder.Aggregations, PivotBuilder.DimensionsCache);
            }

            return columnGroupManager;
        }

        protected override void InitializePivotGroupers(IDimensionCache dimensionCache, IHierarchicalDimensionCache hierarchicalDimensionCache, IHierarchicalDimensionOptions hierarchicalDimensionOptions)
        {
            foreach (var groupConfig in PivotBuilder.ColumnGroupConfig)
            {
                groupConfig.Grouping.Initialize(dimensionCache);
                if (groupConfig.Grouping is IHierarchicalGrouper<ColumnGroup, T> hgr)
                    hgr.InitializeHierarchies(hierarchicalDimensionCache, hierarchicalDimensionOptions);
            }
            
            foreach (var groupConfig in PivotBuilder.RowGroupConfig)
            {
                groupConfig.Grouping.Initialize(dimensionCache);
                if (groupConfig.Grouping is IHierarchicalGrouper<RowGroup, T> hgr)
                    hgr.InitializeHierarchies(hierarchicalDimensionCache, hierarchicalDimensionOptions);
            }
        }

        protected override void SetMaxLevelForHierarchicalGroupers(T[] transformed)
        {
            foreach (var groupConfig in PivotBuilder.ColumnGroupConfig)
            {
                if (groupConfig.Grouping is IHierarchicalGrouper<ColumnGroup, T> hgr)
                    foreach (var element in transformed)
                    {
                        hgr.UpdateMaxLevel(element);
                    }
            }

            foreach (var groupConfig in PivotBuilder.RowGroupConfig)
            {
                if (groupConfig.Grouping is IHierarchicalGrouper<RowGroup, T> hgr)
                    foreach (var element in transformed)
                    {
                        hgr.UpdateMaxLevel(element);
                    }
            }
        }

        protected override PivotGroupManager<T, TIntermediate, TAggregate, RowGroup> GetRowGroupManager()
        {
            PivotGroupManager<T, TIntermediate, TAggregate, RowGroup> rowGroupManager = null;
            foreach (var groupConfig in PivotBuilder.RowGroupConfig)
            {
                rowGroupManager = groupConfig.GetGroupManager(rowGroupManager, PivotBuilder.Aggregations, PivotBuilder.DimensionsCache);
            }

            return rowGroupManager;
        }

        public override PivotModel Execute()
        {
            PivotBuilder = PivotBuilder with { Transformation = x => x };
            return base.Execute();
        }
    }
}
