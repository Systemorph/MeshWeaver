using OpenSmc.Charting.Builders.ChartBuilders;
using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models;
using OpenSmc.Charting.Models.Options.Scales;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Models;

namespace OpenSmc.Charting.Pivot;

public record PivotBarChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    : PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, BarChartBuilder, BarDataSet, BarOptionsBuilder, BarDataSetBuilder>, IPivotBarChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    public PivotBarChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        ChartBuilder = new BarChartBuilder();
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
                        Rows = model.Rows.Where(row => row.Descriptor.Coordinates.Count == 1 || rowGroupingsCount == row.Descriptor.Coordinates.Count)
                                    .Select(row => row.Descriptor.Coordinates.Count == 1
                                                       ? row with { DataSetType = ChartType.Scatter }
                                                       : row with { Stack = row.Descriptor.Coordinates.First().SystemName })
                                    .ToList()
                    };

            return model;
        }

        PivotChartModelBuilder.AddPostProcessor(NewPostProcessor);
    }

    private void RenderRowsToLines(IList<string> rowLineNames)
    {
        PivotChartModelBuilder.AddPostProcessor(model => model with
                                                     {
                                                         Rows = model.Rows
                                                                 .Select(row => rowLineNames.Contains(row.Descriptor.SystemName) || 
                                                                                rowLineNames.Contains(row.Descriptor.DisplayName) ? 
                                                                                    row with {DataSetType = ChartType.Line} : 
                                                                                    row)
                                                                 .ToList()
                                                     });
    }

    protected override PivotChartModel CreatePivotModel(PivotModel pivotModel)
    {
        return PivotChartModelBuilder.BuildFromPivotModel(pivotModel, ChartType.Bar);
    }

    protected override void AddDataSets(PivotChartModel pivotChartModel)
    {
        var countStackPoints = 0;
        var totalNbStackPoints = pivotChartModel.Rows.Count(row => row.DataSetType == ChartType.Scatter);
        foreach (var row in pivotChartModel.Rows)
        {
            switch (row.DataSetType)
            {
                case ChartType.Bar when row.Stack != null:
                {
                    ChartBuilder.WithDataSet(builder => builder.WithData(row.DataByColumns.Select(x => x.Value))
                                                               .SetType(ChartType.Bar)
                                                               .WithXAxis(PivotChartConst.XBarAxis)
                                                               .WithLabel(row.Descriptor.DisplayName)
                                                               .WithStack(row.Stack));
                    break;
                }

                case ChartType.Bar:
                {
                    ChartBuilder.WithDataSet(builder => builder.WithData(row.DataByColumns.Select(x => x.Value))
                                                               .SetType(ChartType.Bar)
                                                               .WithXAxis(PivotChartConst.XBarAxis)
                                                               .WithLabel(row.Descriptor.DisplayName));
                    break;
                }

                case ChartType.Scatter:
                {
                    var shift = -0.4 + (0.4 / totalNbStackPoints) * (2 * countStackPoints + 1) + 1; // fix this! plus one was added just to have correct numbers in one example
                    var dataSet = row.DataByColumns.Select((x, i) => (i + shift, x.Value ?? 0)).ToList();
                    ChartBuilder.WithDataSet<LineScatterDataSetBuilder, LineScatterDataSet>(builder => builder.WithDataPoint(dataSet)
                                                                                                              .WithXAxis(PivotChartConst.XScatterAxis)
                                                                                                              .WithLabel(row.Descriptor.DisplayName + ", total")
                                                                                                              .WithPointStyle(Shapes.Rectangle)
                                                                                                              .WithPointRadius(4));
                    countStackPoints++;
                    break;
                }

                case ChartType.Line:
                {
                    ChartBuilder.WithDataSet(builder => builder.WithData(row.DataByColumns.Select(x => x.Value))
                                                               .SetType(ChartType.Line)
                                                               .WithXAxis(PivotChartConst.XBarAxis)
                                                               .WithLabel(row.Descriptor.DisplayName));
                    break;
                }

                    default:
                    throw new NotImplementedException("Only bar, line and scatter data set types are supported");
            }
        }
    }

    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
        AddScales(pivotChartModel);
    }

    protected void AddScales(PivotChartModel pivotChartModel)
    {
        var labels = pivotChartModel.ColumnDescriptors.Select(x => x.DisplayName).ToList();
        var linearScaleMax = labels.Count;
        var dataSetTypes = pivotChartModel.Rows.Select(row => row.DataSetType).ToHashSet();
        var barStacked = pivotChartModel.Rows.Any(row => row.DataSetType == ChartType.Bar && row.Stack != null);
        
        Dictionary<string, Scale> scales = new();

        foreach (var dataSetType in dataSetTypes)
        {
            switch (dataSetType)
            {
                case ChartType.Bar: // do we have to make sure this axis goes first?
                    scales.Add(PivotChartConst.XBarAxis, new CartesianCategoryScale
                                                         {
                                                             Stacked = barStacked,
                                                             Labels = labels,
                                                             Display = true
                                                         });
                    break;
                case ChartType.Scatter:
                    scales.Add(PivotChartConst.XScatterAxis, new CartesianLinearScale()
                                                             {
                                                                 Stacked = "false",
                                                                 Display = false,
                                                                 Min = 1,
                                                                 Max = linearScaleMax,
                                                                 Ticks = new CartesianLinearTick() { StepSize = 1 },
                                                                 Type = "linear"
                                                             });
                    break;
                case ChartType.Line:
                    break;
                default: 
                    throw new NotImplementedException("Only bar, line and scatter data set types are supported");
            }
        
        }

        scales.Add(PivotChartConst.YAxis, new Scale { Stacked = barStacked });

        ChartBuilder = ChartBuilder.WithOptions(o => o.WithScales(scales).Responsive());

    }
}