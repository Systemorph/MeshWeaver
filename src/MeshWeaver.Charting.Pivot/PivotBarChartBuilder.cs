using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Line;
using MeshWeaver.Charting.Models.Options.Scales;
using MeshWeaver.Charting.Models.Options.Scales.Ticks;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Charting.Pivot;

public record PivotBarChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    : PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, BarDataSet>,
        IPivotBarChartBuilder
    where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
{
    public PivotBarChartBuilder(
        PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder
    )
        : base(pivotBuilder)
    {
    }


    public new IPivotBarChartBuilder WithOptions(Func<PivotChartModel, PivotChartModel> postProcessor)
    {
        PivotChartModelBuilder.AddPostProcessor(postProcessor);
        return this;
    }

    public IPivotBarChartBuilder WithChartBuilder(Func<Models.ChartModel, Models.ChartModel> builder)
    {
        return this with { Chart = builder(Chart), };
    }

    public IPivotBarChartBuilder AsStackedWithScatterTotals()
    {
        StackedWithTotalsAsScatter();
        return this;
    }

    public IPivotChartBuilder WithRowsAsLine(params string[] rowLinesNames)
    {
        RenderRowsToLines(rowLinesNames.ToList());
        return this;
    }

    private void StackedWithTotalsAsScatter()
    {
        PivotChartModel NewPostProcessor(PivotChartModel model)
        {
            var rowGroupingsCount = model.RowGroupings.Count;

            if (rowGroupingsCount == 1)
                return model;

            model = model with
            {
                Rows = model
                    .Rows.Where(row =>
                        row.Descriptor.Coordinates.Count == 1
                        || rowGroupingsCount == row.Descriptor.Coordinates.Count
                    )
                    .Select(row =>
                        row.Descriptor.Coordinates.Count == 1
                            ? row with
                            {
                                DataSetType = ChartType.Scatter
                            }
                            : row with
                            {
                                Stack = row.Descriptor.Coordinates.First().Id
                            }
                    )
                    .ToList()
            };

            return model;
        }

        PivotChartModelBuilder.AddPostProcessor(NewPostProcessor);
    }

    private void RenderRowsToLines(IList<string> rowLineNames)
    {
        PivotChartModelBuilder.AddPostProcessor(model =>
            model with
            {
                Rows = model
                    .Rows.Select(row =>
                        rowLineNames.Contains(row.Descriptor.Id)
                        || rowLineNames.Contains(row.Descriptor.DisplayName)
                            ? row with
                            {
                                DataSetType = ChartType.Line
                            }
                            : row
                    )
                    .ToList()
            }
        );
    }

    protected override PivotChartModel CreatePivotModel(PivotModel pivotModel)
    {
        return PivotChartModelBuilder.BuildFromPivotModel(pivotModel, ChartType.Bar);
    }



}
