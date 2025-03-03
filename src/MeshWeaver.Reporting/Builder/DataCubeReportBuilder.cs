﻿using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.GridModel;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Builder.Interfaces;
using MeshWeaver.Reporting.Builder.Interfaces;
using MeshWeaver.Reporting.Models;

namespace MeshWeaver.Reporting.Builder
{
    public record DataCubeReportBuilder<TCube, TElement, TIntermediate, TAggregate>
        : IDataCubePivotBuilder<
            TCube,
            TElement,
            TIntermediate,
            TAggregate,
            DataCubeReportBuilder<TCube, TElement, TIntermediate, TAggregate>
        >,
            IReportBuilder<DataCubeReportBuilder<TCube, TElement, TIntermediate, TAggregate>>
        where TCube : IDataCube<TElement>
    {
        public IWorkspace Workspace { get; }
        public EntityStore State { get; }
        private readonly IList<Func<GridOptions, GridOptions>> postProcessors = new List<
            Func<GridOptions, GridOptions>
        >()
        {
            x => x
        };
        private DataCubePivotBuilder<
            TCube,
            TElement,
            TIntermediate,
            TAggregate
        > PivotBuilder { get; init; }

        private DataCubeReportBuilder(IWorkspace workspace, IEnumerable<TCube> cubes)
        {
            Workspace = workspace;
            PivotBuilder = new DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate>(
                workspace,
                cubes
            );
        }

        public DataCubeReportBuilder(
            DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate> pivotBuilder,
            Func<GridOptions, GridOptions> gridOptionsPostProcessor = null
        )
        {
            PivotBuilder = pivotBuilder;
            if (gridOptionsPostProcessor is not null)
                postProcessors.Add(gridOptionsPostProcessor);
        }

        public DataCubeReportBuilder(
            IWorkspace workspace,
            EntityStore state,
            IEnumerable<TCube> cubes,
            Aggregations<DataSlice<TElement>, TIntermediate, TAggregate> aggregations
        )
        {
            State = state;
            PivotBuilder = new DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate>(
                workspace,
                cubes
            )
            {
                Aggregations = aggregations
            };
        }

        public DataCubeReportBuilder<
            TCube,
            TElement,
            TNewIntermediate,
            TNewAggregate
        > WithAggregation<TNewIntermediate, TNewAggregate>(
            Func<
                Aggregations<TElement, TIntermediate, TAggregate>,
                Aggregations<TElement, TNewIntermediate, TNewAggregate>
            > aggregationsFunc
        )
        {
            var aggregations = aggregationsFunc(
                new Aggregations<TElement, TIntermediate, TAggregate>()
            );

            var builder = new DataCubeReportBuilder<
                TCube,
                TElement,
                TNewIntermediate,
                TNewAggregate
            >(Workspace, PivotBuilder.Objects);

            return builder with
            {
                PivotBuilder = builder.PivotBuilder with
                {
                    SliceColumns = PivotBuilder.SliceColumns,
                    SliceRows = PivotBuilder.SliceRows,
                    Aggregations = new Aggregations<
                        DataSlice<TElement>,
                        TNewIntermediate,
                        TNewAggregate
                    >
                    {
                        Aggregation = slices =>
                            aggregations.Aggregation(slices.Select(s => s.Data)),
                        AggregationOfAggregates = aggregations.AggregationOfAggregates,
                        ResultTransformation = aggregations.ResultTransformation,
                        Name = aggregations.Name
                    }
                }
            };
        }

        public DataCubeReportBuilder<
            TCube,
            TElement,
            TNewAggregate,
            TNewAggregate
        > WithAggregation<TNewAggregate>(
            Func<
                Aggregations<TElement, TIntermediate, TAggregate>,
                Aggregations<TElement, TNewAggregate>
            > aggregationsFunc
        )
        {
            var aggregations = aggregationsFunc(
                new Aggregations<TElement, TIntermediate, TAggregate>()
            );

            var builder = new DataCubeReportBuilder<TCube, TElement, TNewAggregate, TNewAggregate>(
                Workspace,
                PivotBuilder.Objects
            );

            return builder with
            {
                PivotBuilder = builder.PivotBuilder with
                {
                    SliceColumns = PivotBuilder.SliceColumns,
                    SliceRows = PivotBuilder.SliceRows,
                    Aggregations = new Aggregations<
                        DataSlice<TElement>,
                        TNewAggregate,
                        TNewAggregate
                    >
                    {
                        Aggregation = slices =>
                            aggregations.Aggregation(slices.Select(s => s.Data)),
                        AggregationOfAggregates = aggregations.AggregationOfAggregates,
                        ResultTransformation = aggregations.ResultTransformation,
                        Name = aggregations.Name
                    }
                }
            };
        }

        public DataCubeReportBuilder<TCube, TElement, TIntermediate, TAggregate> WithOptions(
            Func<GridOptions, GridOptions> gridOptions
        )
        {
            if (gridOptions is not null)
                postProcessors.Add(gridOptions);
            return this;
        }

        public DataCubeReportBuilder<
            TCube,
            TElement,
            TIntermediate,
            TAggregate
        > WithHierarchicalDimensionOptions(
            Func<IHierarchicalDimensionOptions, IHierarchicalDimensionOptions> optionsFunc
        )
        {
            return this with
            {
                PivotBuilder = PivotBuilder.WithHierarchicalDimensionOptions(optionsFunc)
            };
        }

        public DataCubeReportBuilder<TCube, TElement, TIntermediate, TAggregate> SliceColumnsBy(
            params string[] dimensions
        )
        {
            return this with { PivotBuilder = PivotBuilder.SliceColumnsBy(dimensions) };
        }

        public DataCubeReportBuilder<TCube, TElement, TIntermediate, TAggregate> SliceRowsBy(
            params string[] dimensions
        )
        {
            return this with { PivotBuilder = PivotBuilder.SliceRowsBy(dimensions) };
        }

        public DataCubeReportBuilder<TCube, TElement, TIntermediate, TAggregate> WithAggregation(
            Func<
                Aggregations<TElement, TIntermediate, TAggregate>,
                Aggregations<TElement, TIntermediate, TAggregate>
            > aggregationsFunc
        )
        {
            return this with { PivotBuilder = PivotBuilder.WithAggregation(aggregationsFunc) };
        }

        public DataCubeReportBuilder<TCube, TElement, TIntermediate, TAggregate> Transpose<TValue>()
        {
            return this with { PivotBuilder = PivotBuilder.Transpose<TValue>() };
        }

        public virtual IObservable<GridOptions> Execute()
        {
            return  PivotBuilder.Execute()
                .Select(pivotModel =>
                {
                    var gridOptions = GridOptionsMapper.MapToGridOptions(pivotModel);

                    GridOptions GridOptionsPostProcessor(GridOptions go) =>
                        postProcessors.Aggregate(go, (current, next) => next(current));
                    gridOptions = GridOptionsPostProcessor(gridOptions);

                    return gridOptions;
                });

        }

        public GridControl ToGridControl() => new(Execute());
    }
}
