using System.Reactive.Linq;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Line;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Models.Options.Scales;
using MeshWeaver.Charting.Models.Options.Scales.Ticks;
using MeshWeaver.Charting.Models.Radar;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Charting.Pivot;


public abstract record PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, TDataSet>
    : IPivotChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    where TDataSet : DataSet
{
    private readonly PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder;
    protected ChartModel Chart = new();
    protected PivotChartModelBuilder PivotChartModelBuilder { get; init; } = new();

    protected PivotChartBuilderBase(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder
    )
    {
        this.pivotBuilder = pivotBuilder;
    }

    public IPivotChartBuilder WithLegend(Func<Legend, Legend> legendModifier = null)
    {
        Chart = Chart.WithLegend(legendModifier);
        return this;
    }

    public IPivotChartBuilder WithTitle(string title, Func<Title, Title> titleModifier)
    {
        Chart = Chart.WithTitle(title, titleModifier);
        return this;
    }

    public IPivotChartBuilder WithSubTitle(string title, Func<Title, Title> titleModifier)
    {
        Chart = Chart.WithSubTitle(title, titleModifier);
        return this;
    }

    public IPivotChartBuilder WithColorScheme(string[] scheme)
    {
        Chart = Chart.WithColorPalette(scheme);
        return this;
    }

    public IPivotChartBuilder WithColorScheme(Palettes scheme)
    {
        Chart = Chart.WithColorPalette(scheme);
        return this;
    }

    public IObservable<ChartModel> Execute()
    {
        return pivotBuilder.Execute()
            .Select(pivotModel =>
            {
                var pivotChartModel = CreatePivotModel(pivotModel);
                AddDataSets(pivotChartModel);
                AddOptions(pivotChartModel);

                ApplyCustomChartConfigs();
                return Chart;
            });
    }

    public IPivotChartBuilder WithOptions(Func<PivotChartModel, PivotChartModel> postProcessor)
    {
        PivotChartModelBuilder.AddPostProcessor(postProcessor);
        return this;
    }

    public IPivotChartBuilder WithRows(params string[] lineRows)
    {
        PivotChartModelBuilder.AddPostProcessor(model =>
            model with
            {
                Rows = model
                    .Rows.Where(row =>
                        lineRows.Contains(row.Descriptor.Id)
                        || lineRows.Contains(row.Descriptor.DisplayName)
                    )
                    .ToList()
            }
        );
        return this;
    }

    protected abstract PivotChartModel CreatePivotModel(PivotModel pivotModel);

    protected virtual void AddDataSets(PivotChartModel pivotChartModel)
    {
        var countStackPoints = 0;

        foreach (var row in pivotChartModel.Rows)
        {
            var values = pivotChartModel.ColumnDescriptors.Select(c =>
                    row.DataByColumns
                        .FirstOrDefault(x => x.ColSystemName == c.Id).Value)
                .Cast<object>()
                .ToArray();

            var dataSet = CreateDataSet(
                pivotChartModel,
                values,
                row,
                ref countStackPoints
            ) with{Label = row.Descriptor.DisplayName };
            dataSet = dataSet is not IDataSetWithFill<TDataSet> fill ? dataSet : row.Filled  ? fill.WithArea() : fill.WithoutFill();
            dataSet = dataSet is not IDataSetWithTension<TDataSet> smoothed
                ? dataSet 
                : row.SmoothingCoefficient != null 
                    ?
                    smoothed.Smoothed((double)row.SmoothingCoefficient) :
                dataSet;
            Chart = Chart.WithDataSet(dataSet);
        }


        Chart = Chart.WithLabels(pivotChartModel.ColumnDescriptors.Select(x => x.DisplayName).ToArray());
    }


    protected virtual DataSet CreateDataSet(PivotChartModel pivotChartModel, IReadOnlyCollection<object> values, PivotChartRow row, ref int countStackPoints)
    {

        return row.DataSetType switch
        {
            ChartType.Bar when row.Stack != null =>
                new BarDataSet(values)
                    .WithXAxis(PivotChartConst.XBarAxis)
                    .WithLabel(row.Descriptor.DisplayName)
            .WithStack(row.Stack),

            ChartType.Bar => new BarDataSet(values.Cast<object>().ToArray())
                .WithXAxis(PivotChartConst.XBarAxis)
                .WithLabel(row.Descriptor.DisplayName),

            ChartType.Scatter => 
                
            CreateScatterDataSet(pivotChartModel, values, row,  ref countStackPoints),
            ChartType.Line => new LineDataSet(values)
                .WithXAxis(PivotChartConst.XBarAxis)
                .WithLabel(row.Descriptor.DisplayName),
            ChartType.Radar => new RadarDataSet(values)
                .WithLabel(row.Descriptor.DisplayName),

            _ => throw new NotImplementedException(
                "Only bar, line and scatter data set types are supported"
            )

        };
    }

    private static ScatterDataSet CreateScatterDataSet(PivotChartModel pivotChartModel,
        IReadOnlyCollection<object> values, PivotChartRow row, ref int countStackPoints)
    {
        var totalNbStackPoints = pivotChartModel.Rows.Count(r => r.DataSetType == ChartType.Scatter);
        var shift = -0.4 + (0.4 / totalNbStackPoints) * (2 * countStackPoints + 1) +
                    1; // fix this! plus one was added just to have correct numbers in one example
        var dataPairs = values
            .Select((value, i) => (i + shift, Convert.ToDouble(value ?? 0)))
            .Cast<object>()
            .ToList();
        countStackPoints++;
        return new ScatterDataSet(dataPairs)
            .WithXAxis(PivotChartConst.XScatterAxis)
            .WithPointStyle(Shapes.Rectangle)
            .WithPointRadius(4);
    }

    protected virtual void AddOptions(PivotChartModel pivotChartModel)
    {
        AddScales(pivotChartModel);
    }

    protected void AddScales(PivotChartModel pivotChartModel)
    {
        var labels = pivotChartModel
            .ColumnDescriptors
            .Select(x => x.DisplayName)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToList();
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
            scales.Add(PivotChartConst.YAxis, new Scale { Stacked = barStacked });
        }

        Chart = Chart.WithOptions(o => o.WithScales(scales).WithResponsive());
    }

    protected virtual void ApplyCustomChartConfigs()
    {
    }
}
