using MeshWeaver.Data;
using MeshWeaver.Hierarchies;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Pivot.Processors
{
    public class PivotProcessor<T, TIntermediate, TAggregate>
        : PivotProcessorBase<
            T,
            T,
            TIntermediate,
            TAggregate,
            PivotBuilder<T, TIntermediate, TAggregate>
        >
    {
        public PivotProcessor(
            IPivotConfiguration<TAggregate, ColumnGroup> colConfig,
            IPivotConfiguration<TAggregate, RowGroup> rowConfig,
            PivotBuilder<T, TIntermediate, TAggregate> pivotBuilder
        )
            : base(pivotBuilder)
        {
            ColumnConfig = colConfig;
            RowConfig = rowConfig;
        }

        protected override PivotGroupManager<
            T,
            TIntermediate,
            TAggregate,
            ColumnGroup
        > GetColumnGroupManager()
        {
            PivotGroupManager<T, TIntermediate, TAggregate, ColumnGroup> columnGroupManager = null;

            foreach (var groupConfig in PivotBuilder.ColumnGroupConfig)
            {
                columnGroupManager = groupConfig.GetGroupManager(
                    columnGroupManager,
                    PivotBuilder.Aggregations
                );
            }

            return columnGroupManager;
        }

        protected override void SetMaxLevelForHierarchicalGroupers(
            IReadOnlyCollection<T> transformed
        )
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

        protected override PivotGroupManager<
            T,
            TIntermediate,
            TAggregate,
            RowGroup
        > GetRowGroupManager()
        {
            PivotGroupManager<T, TIntermediate, TAggregate, RowGroup> rowGroupManager = null;
            foreach (var groupConfig in PivotBuilder.RowGroupConfig)
            {
                rowGroupManager = groupConfig.GetGroupManager(
                    rowGroupManager,
                    PivotBuilder.Aggregations
                );
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
