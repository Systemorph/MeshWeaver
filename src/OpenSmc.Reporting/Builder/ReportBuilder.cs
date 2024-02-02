using System.Linq.Expressions;
using OpenSmc.GridModel;
using OpenSmc.Pivot.Aggregations;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Builder.Interfaces;
using OpenSmc.Reporting.Builder.Interfaces;
using OpenSmc.Reporting.Models;

namespace OpenSmc.Reporting.Builder
{
    public record ReportBuilder<T, TIntermediate, TAggregate> : IPivotBuilder<T, TIntermediate, TAggregate, ReportBuilder<T, TIntermediate, TAggregate>>,
                                                                       IReportBuilder<ReportBuilder<T, TIntermediate, TAggregate>>
    {
        private readonly IList<Func<GridOptions, GridOptions>> postProcessors = new List<Func<GridOptions, GridOptions>>() { x => x };
        private PivotBuilder<T, TIntermediate, TAggregate> PivotBuilder { get; init; }

        public ReportBuilder(PivotBuilder<T, TIntermediate, TAggregate> pivotBuilder, Func<GridOptions, GridOptions> gridOptionsPostProcessor = null)
        {
            PivotBuilder = pivotBuilder;
            if (gridOptionsPostProcessor is not null) postProcessors.Add(gridOptionsPostProcessor);
        }

        private ReportBuilder(IEnumerable<T> objects)
        {
            PivotBuilder = new PivotBuilder<T, TIntermediate, TAggregate>(objects);
        }

        public ReportBuilder<T, TNewIntermediate, TNewAggregate> WithAggregation<TNewIntermediate, TNewAggregate>(Func<Aggregations<T, TIntermediate, TAggregate>,
                                                                                                                                        Aggregations<T, TNewIntermediate, TNewAggregate>>
                                                                                                                                    aggregationsFunc)
        {
            var builder = new ReportBuilder<T, TNewIntermediate, TNewAggregate>(PivotBuilder.Objects);
            return builder with
                   {
                       PivotBuilder = builder.PivotBuilder with
                                      {
                                          Aggregations = aggregationsFunc(new Aggregations<T, TIntermediate, TAggregate>()),
                                      }
                   };
        }

        public ReportBuilder<T, TNewAggregate, TNewAggregate> WithAggregation<TNewAggregate>(Func<Aggregations<T, TIntermediate, TAggregate>,
                                                                                                               Aggregations<T, TNewAggregate>>
                                                                                                               aggregationsFunc)
        {
            var builder = new ReportBuilder<T, TNewAggregate, TNewAggregate>(PivotBuilder.Objects);
            return builder with
                   {
                       PivotBuilder = builder.PivotBuilder with
                                      {
                                          Aggregations = aggregationsFunc(new Aggregations<T, TIntermediate, TAggregate>()),
                                      }
                   };
        }

        public ReportBuilder<T, TIntermediate, TAggregate> WithAggregation(Func<Aggregations<T, TIntermediate, TAggregate>,
                                                                                                 Aggregations<T, TIntermediate, TAggregate>>
                                                                                             aggregationsFunc)
        {
            return this with { PivotBuilder = PivotBuilder.WithAggregation(aggregationsFunc) };
        }

        public ReportBuilder<T, TIntermediate, TAggregate> WithOptions(Func<GridOptions, GridOptions> gridOptions)
        {
            if (gridOptions is not null) postProcessors.Add(gridOptions);
            return this;
        }

        public ReportBuilder<T, TIntermediate, TAggregate> WithHierarchicalDimensionOptions(Func<IHierarchicalDimensionOptions, IHierarchicalDimensionOptions> optionsFunc)
        {
            return this with { PivotBuilder = PivotBuilder.WithHierarchicalDimensionOptions(optionsFunc) };
        }

        public ReportBuilder<T, TIntermediate, TAggregate> GroupRowsBy<TSelected>(Expression<Func<T, TSelected>> selector)
        {
            return this with { PivotBuilder = PivotBuilder.GroupRowsBy(selector) };
        }

        public ReportBuilder<T, TIntermediate, TAggregate> GroupColumnsBy<TSelected>(Expression<Func<T, TSelected>> selector)
        {
            return this with { PivotBuilder = PivotBuilder.GroupColumnsBy(selector) };
        }

        public ReportBuilder<T, TIntermediate, TAggregate> Transpose<TValue>()
        {
            return this with { PivotBuilder = PivotBuilder.Transpose<TValue>() };
        }

        public virtual GridOptions Execute()
        {
            var pivotModel = PivotBuilder.Execute();

            var gridOptions = GridOptionsMapper.MapToGridOptions(pivotModel);

            GridOptions PostProcessor(GridOptions go) => postProcessors.Aggregate(go, (current, next) => next(current));
            gridOptions = PostProcessor(gridOptions);

            return gridOptions;
        }
    }
}
