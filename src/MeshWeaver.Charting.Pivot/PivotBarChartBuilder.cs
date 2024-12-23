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
    : PivotChartBuilderBase<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >,
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

    protected override void AddDataSets(PivotChartModel pivotChartModel)
    {
        var countStackPoints = 0;
        var totalNbStackPoints = pivotChartModel.Rows.Count(row =>
            row.DataSetType == ChartType.Scatter
        );

        foreach (var row in pivotChartModel.Rows)
        {
            var values = pivotChartModel.ColumnDescriptors.Select(c =>
                row.DataByColumns.FirstOrDefault(x => x.ColSystemName == c.Id).Value);

            var dataSet = row.DataSetType switch
            {
                ChartType.Bar when row.Stack != null =>
                    AddDataSet(ChartType.Bar,
                        new BarDataSet(values.Cast<object>().ToArray())
                            .WithXAxis(PivotChartConst.XBarAxis)
                            .WithLabel(row.Descriptor.DisplayName)
                            .WithStack(row.Stack)),

                ChartType.Bar => AddDataSet(ChartType.Bar,
                    new BarDataSet(values.Cast<object>().ToArray())
                        .WithXAxis(PivotChartConst.XBarAxis)
                        .WithLabel(row.Descriptor.DisplayName)
                ),

                ChartType.Scatter => AddDataSet(ChartType.Scatter,
                    new ScatterDataSet(
                            values.Select((value, i) => (
                                    i + -0.4 + (0.4 / totalNbStackPoints) * (2 * countStackPoints + 1) + 1, value ?? 0))
                                .Cast<object>().ToArray()
                        )
                        .WithXAxis(PivotChartConst.XScatterAxis)
                        .WithLabel(row.Descriptor.DisplayName + ", total")
                        .WithPointStyle(Shapes.Rectangle)
                        .WithPointRadius(4)),
                ChartType.Line => AddDataSet(ChartType.Line,
                    new BarDataSet(values.Cast<object>().ToArray())
                        .WithXAxis(PivotChartConst.XBarAxis)
                        .WithLabel(row.Descriptor.DisplayName)),
                _ => throw new NotImplementedException(
                        "Only bar, line and scatter data set types are supported"
                    )
                
            };
        }
    }

    private ChartModel AddDataSet(ChartType type, DataSet dataSet) => Chart?.WithDataSet(dataSet) ?? new(dataSet);

    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
        AddScales(pivotChartModel);
    }

    protected void AddScales(PivotChartModel pivotChartModel)
    {
        var labels = pivotChartModel.ColumnDescriptors.Select(x => x.DisplayName).ToList();
        var linearScaleMax = labels.Count;
        var dataSetTypes = pivotChartModel.Rows.Select(row => row.DataSetType).ToHashSet();
        var barStacked = pivotChartModel.Rows.Any(row =>
            row.DataSetType == ChartType.Bar && row.Stack != null
        );

        Dictionary<string, Scale> scales = new();

        foreach (var dataSetType in dataSetTypes)
        {
            switch (dataSetType)
            {
                case ChartType.Bar: // do we have to make sure this axis goes first?
                    scales.Add(
                        Chart.IsHorizontal() ? PivotChartConst.YAxis : PivotChartConst.XBarAxis,
                        new CartesianCategoryScale
                        {
                            Stacked = barStacked,
                            Labels = labels,
                            Display = true
                        }
                    );
                    break;
                case ChartType.Scatter:
                    scales.Add(
                        PivotChartConst.XScatterAxis,
                        new CartesianLinearScale()
                        {
                            Stacked = "false",
                            Display = false,
                            Min = 1,
                            Max = linearScaleMax,
                            Ticks = new CartesianLinearTick() { StepSize = 1 },
                            Type = "linear"
                        }
                    );
                    break;
                case ChartType.Line:
                    break;
                default:
                    throw new NotImplementedException(
                        "Only bar, line and scatter data set types are supported"
                    );
            }
        }

        if (!Chart.IsHorizontal())
        {
            scales.Add(PivotChartConst.YAxis, new Scale {Stacked = barStacked});
        }

        Chart = Chart.WithOptions(o => o.WithScales(scales).WithResponsive());
    }
}
