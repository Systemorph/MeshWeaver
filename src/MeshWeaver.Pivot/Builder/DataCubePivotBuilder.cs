﻿using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Arithmetics;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Domain;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder.Interfaces;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models;
using MeshWeaver.Pivot.Processors;
using MeshWeaver.Reflection;

namespace MeshWeaver.Pivot.Builder
{
    public record DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate>
        : PivotBuilderBase<
            TCube,
            DataSlice<TElement>,
            TIntermediate,
            TAggregate,
            DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate>
        >,
            IDataCubePivotBuilder<
                TCube,
                TElement,
                TIntermediate,
                TAggregate,
                DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate>
            >
        where TCube : IDataCube<TElement>
    {
        public SlicePivotGroupingConfigItem<TElement, RowGroup> SliceRows { get; init; }
        public SlicePivotGroupingConfigItem<TElement, ColumnGroup> SliceColumns { get; init; }
        protected IImmutableList<string> PropertiesToHide { get; init; } =
            ImmutableList<string>.Empty;
        private DimensionDescriptor[] AggregateByDimensionDescriptors { get; init; } =
            Array.Empty<DimensionDescriptor>();
        private string[] ManualSliceRowsDimensionDescriptors { get; init; } = Array.Empty<string>();

        public DataCubePivotBuilder(IWorkspace workspace, IEnumerable<TCube> cubes)
            : base(workspace, cubes)
        {
            AggregateByDimensionDescriptors = GetAggregationPropertiesDescriptors().ToArray();
            SliceColumns = new SlicePivotGroupingConfigItem<TElement, ColumnGroup>(
                [],
                HierarchicalDimensionOptions
            );
            SliceRows = new SlicePivotGroupingConfigItem<TElement, RowGroup>(
                GetAggregateBySliceRowsDimensionDescriptors(this),
                HierarchicalDimensionOptions
            );
        }

        public DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate> SliceRowsBy(
            params string[] dimensions
        )
        {
            var builder = this with
            {
                ManualSliceRowsDimensionDescriptors = ManualSliceRowsDimensionDescriptors
                    .Union(dimensions)
                    .ToArray()
            };

            builder = builder with
            {
                AggregateByDimensionDescriptors = AggregateByDimensionDescriptors
                    .Except(GetManualSliceRowsDimensionDescriptors(builder))
                    .ToArray()
            };

            builder = builder with
            {
                SliceRows = new SlicePivotGroupingConfigItem<TElement, RowGroup>(
                    GetSliceRowsDimensionDescriptors(builder),
                    HierarchicalDimensionOptions
                ),
                PropertiesToHide = dimensions.ToImmutableList()
            };

            return builder;
        }

        public DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate> SliceColumnsBy(
            params string[] dimensions
        )
        {
            var dimensionDescriptors = GetDimensionDescriptors(this, false, dimensions).ToArray();

            var builder = this with
            {
                AggregateByDimensionDescriptors = AggregateByDimensionDescriptors
                    .Except(dimensionDescriptors)
                    .ToArray(),
            };

            builder = builder with
            {
                SliceColumns = new SlicePivotGroupingConfigItem<TElement, ColumnGroup>(
                    SliceColumns
                        ?.Dimensions.Select(d => d.Dim)
                        .Union(dimensionDescriptors)
                        .ToArray() ?? dimensionDescriptors,
                    HierarchicalDimensionOptions
                ),
                SliceRows = new SlicePivotGroupingConfigItem<TElement, RowGroup>(
                    GetSliceRowsDimensionDescriptors(builder),
                    HierarchicalDimensionOptions
                )
            };

            return builder;
        }

        private static IEnumerable<DimensionDescriptor> GetAggregationPropertiesDescriptors()
        {
            return typeof(TElement)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(x => x.HasAttribute<AggregateByAttribute>())
                .Select(x =>
                {
                    var dimensionAttribute = x.GetCustomAttribute<DimensionAttribute>();
                    if (dimensionAttribute != null)
                        return new DimensionDescriptor(
                            dimensionAttribute.Name,
                            dimensionAttribute.Type
                        );
                    return new DimensionDescriptor(x.Name, x.PropertyType);
                });
        }

        private DimensionDescriptor[] GetSliceRowsDimensionDescriptors(
            DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate> builder
        )
        {
            var descriptors = GetAggregateBySliceRowsDimensionDescriptors(builder)
                .Union(GetManualSliceRowsDimensionDescriptors(builder))
                .ToArray();

            return descriptors;
        }

        private static DimensionDescriptor[] GetAggregateBySliceRowsDimensionDescriptors(
            DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate> builder
        )
        {
            if (builder.AggregateByDimensionDescriptors.Any())
                // TODO V10: check if we should use TransposedValue == null instead of true in the args (2022/09/06, Ekaterina Mishina)
                return GetDimensionDescriptors(builder, true, Array.Empty<string>())
                    .Union(builder.AggregateByDimensionDescriptors)
                    .ToArray();

            return new DimensionDescriptor[] { };
        }

        private static DimensionDescriptor[] GetManualSliceRowsDimensionDescriptors(
            DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate> builder
        )
        {
            if (builder.ManualSliceRowsDimensionDescriptors.Any())
                return GetDimensionDescriptors(
                    builder,
                    builder.TransposedValue == null,
                    builder.ManualSliceRowsDimensionDescriptors
                );

            return new DimensionDescriptor[] { };
        }

        private static DimensionDescriptor[] GetDimensionDescriptors(
            DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate> builder,
            bool isByRow,
            string[] dimensions
        )
        {
            try
            {
                if (!builder.Objects.Any())
                    return Array.Empty<DimensionDescriptor>();

                return builder
                    .Objects.First()
                    .GetDimensionDescriptors(isByRow, dimensions)
                    .ToArray();
            }
            catch (TypeInitializationException e)
            {
                throw new InvalidOperationException((e.InnerException ?? e).Message, e);
            }
        }

        public DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate> WithAggregation(
            Func<
                Aggregations<TElement, TIntermediate, TAggregate>,
                Aggregations<TElement, TIntermediate, TAggregate>
            > aggregationsFunc
        )
        {
            var aggregations = aggregationsFunc(
                new Aggregations<TElement, TIntermediate, TAggregate>()
            );

            Aggregations = Aggregations with
            {
                Aggregation = slices => aggregations.Aggregation(slices.Select(s => s.Data)),
                AggregationOfAggregates = aggregations.AggregationOfAggregates,
                ResultTransformation = aggregations.ResultTransformation,
                Name = aggregations.Name
            };
            return this;
        }

        public DataCubePivotBuilder<
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

            return new DataCubePivotBuilder<TCube, TElement, TNewIntermediate, TNewAggregate>(
                Workspace,
                Objects
            )
            {
                SliceColumns = SliceColumns,
                SliceRows = SliceRows,
                PropertiesToHide = PropertiesToHide,
                Aggregations = new Aggregations<
                    DataSlice<TElement>,
                    TNewIntermediate,
                    TNewAggregate
                >
                {
                    Aggregation = slices => aggregations.Aggregation(slices.Select(s => s.Data)),
                    AggregationOfAggregates = aggregations.AggregationOfAggregates,
                    ResultTransformation = aggregations.ResultTransformation,
                    Name = aggregations.Name
                }
            };
        }

        public DataCubePivotBuilder<
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

            return new DataCubePivotBuilder<TCube, TElement, TNewAggregate, TNewAggregate>(
                Workspace,
                Objects
            )
            {
                SliceColumns = SliceColumns,
                SliceRows = SliceRows,
                PropertiesToHide = PropertiesToHide,
                Aggregations = new Aggregations<DataSlice<TElement>, TNewAggregate, TNewAggregate>
                {
                    Aggregation = slices => aggregations.Aggregation(slices.Select(s => s.Data)),
                    AggregationOfAggregates = aggregations.AggregationOfAggregates,
                    ResultTransformation = aggregations.ResultTransformation,
                    Name = aggregations.Name
                }
            };
        }

        protected override PivotProcessorBase<
            TCube,
            DataSlice<TElement>,
            TIntermediate,
            TAggregate,
            DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate>
        > GetReportProcessor()
        {
            return new DataCubePivotProcessor<TCube, TElement, TIntermediate, TAggregate>(
                new PivotColumnsConfiguration<TAggregate>(
                    TransposedValue ?? typeof(TAggregate),
                    PropertiesToHide.ToArray()
                ),
                TransposedValue != null
                    ? new TransposedPivotRowsConfiguration<TAggregate>(TransposedValue)
                    : null,
                this with
                {
                    SliceRows =
                        SliceRows
                        ?? new SlicePivotGroupingConfigItem<TElement, RowGroup>(
                            GetAggregateBySliceRowsDimensionDescriptors(this),
                            HierarchicalDimensionOptions
                        ),
                    SliceColumns =
                        SliceColumns
                        ?? new SlicePivotGroupingConfigItem<TElement, ColumnGroup>(
                            Array.Empty<DimensionDescriptor>(),
                            HierarchicalDimensionOptions
                        )
                }
            );
        }
    }
}
