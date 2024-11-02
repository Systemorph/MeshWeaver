using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
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
            PivotBuilder<T, TIntermediate, TAggregate> pivotBuilder,
            IWorkspace workspace
        )
            : base(pivotBuilder, workspace)
        {
            ColumnConfig = colConfig;
            RowConfig = rowConfig;
        }


        protected override PivotGroupManager<T, TIntermediate, TAggregate, ColumnGroup> GetColumnGroupManager(DimensionCache dimensionCache, IReadOnlyCollection<T> transformed)
        {
            PivotGroupManager<T, TIntermediate, TAggregate, ColumnGroup> columnGroupManager = null;

            foreach (var groupConfig in PivotBuilder.ColumnGroupConfig)
            {
                columnGroupManager = groupConfig.GetGroupManager(dimensionCache, columnGroupManager,
                    PivotBuilder.Aggregations
                );
                if (columnGroupManager.Grouper is IHierarchicalGrouper<ColumnGroup, T> hgr)
                    foreach (var element in transformed)
                    {
                        hgr.UpdateMaxLevel(element);
                    }

            }

            return columnGroupManager;
        }

        protected override IObservable<EntityStore> GetStream(IEnumerable<T> objects)
        {
            var types = objects.Select(o => o.GetType()).Distinct().ToArray();
            var dimensions = types
                .SelectMany(t =>
                    t.GetProperties().Select(p => p.GetCustomAttribute<DimensionAttribute>()?.Type))
                .Where(x => x != null)
                .ToArray();
            var reference = dimensions.Select(Workspace.DataContext.TypeRegistry.GetCollectionName)
                .Where(x => x != null).ToArray();
            var stream = reference.Any()
                ? Workspace.GetStream(new CollectionsReference(reference))
                    .Select(x => x.Value)
                : Observable.Return<EntityStore>(new());
            return stream;
        }



        protected override PivotGroupManager<
            T,
            TIntermediate,
            TAggregate,
            RowGroup
        > GetRowGroupManager(DimensionCache dimensionCache, IReadOnlyCollection<T> transformed)
        {
            PivotGroupManager<T, TIntermediate, TAggregate, RowGroup> rowGroupManager = null;
            foreach (var groupConfig in PivotBuilder.RowGroupConfig)
            {
                rowGroupManager = groupConfig.GetGroupManager(dimensionCache, rowGroupManager,
                    PivotBuilder.Aggregations
                );
                if (rowGroupManager.Grouper is IHierarchicalGrouper<RowGroup, T> hgr)
                    foreach (var element in transformed)
                    {
                        hgr.UpdateMaxLevel(element);
                    }

            }

            return rowGroupManager;
        }

    }
}
