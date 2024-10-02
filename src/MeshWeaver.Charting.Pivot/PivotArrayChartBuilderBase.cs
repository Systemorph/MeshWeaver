using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Models;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Charting.Pivot;

public abstract record PivotArrayChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, TDataSet, TDataSetBuilder> :
        PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>, IPivotArrayChartBuilder
        where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
        where TDataSet : DataSet, IDataSetWithPointStyle, IDataSetWithOrder, IDataSetWithFill, IDataSetWithTension, IDataSetWithPointRadiusAndRotation, new()
        where TDataSetBuilder : ArrayDataSetWithTensionFillPointRadiusAndRotation<TDataSetBuilder, TDataSet>, new()
{
    protected PivotArrayChartBuilderBase(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
    }

    public virtual IPivotArrayChartBuilder WithSmoothedLines(params string[] linesToSmooth)
    {
        PivotChartModelBuilder.AddPostProcessor(model => model with
        {
            Rows = model.Rows
                                                                         .Select(row => linesToSmooth
                                                                                            .Contains(row.Descriptor.DisplayName) ?
                                                                                            row with { SmoothingCoefficient = 0.4 }  :
                                                                                            row).ToList()
        });
        return this;
    }


    public virtual IPivotArrayChartBuilder WithSmoothedLines(Dictionary<string, double> smoothDictionary)
    {
        PivotChartModelBuilder.AddPostProcessor(model => model with
        {
            Rows = model.Rows
                                                                         .Select(row => smoothDictionary
                                                                                            .TryGetValue(row.Descriptor.DisplayName, out double s) ?
                                                                                            row with { SmoothingCoefficient = s }  :
                                                                                            row)
                                                                         .ToList()
        });
        return this;
    }

    public IPivotArrayChartBuilder WithFilledArea(params string[] rows)
    {
        PivotChartModelBuilder.AddPostProcessor(model => model with
        {
            Rows = model.Rows.Select(row => !rows.Any() || rows.Contains(row.Descriptor.DisplayName) ?
                                                row with { Filled = true}
                                                : row).ToList()
        });

        return this;
    }

    protected override void AddDataSets(PivotChartModel pivotChartModel)
    {
        foreach (var row in pivotChartModel.Rows)
        {
            var builder = new TDataSetBuilder();
            builder = builder.WithData(row.DataByColumns.Select(x => x.Value))
                             .WithLabel(row.Descriptor.DisplayName);
            builder = row.Filled ? builder.WithArea() : builder.WithoutFill();
            builder = row.SmoothingCoefficient != null ?
                          builder.Smoothed((double)row.SmoothingCoefficient) :
                          builder;
            var dataset = builder.Build();
            Chart = Chart.WithDataSet(dataset);
        }


        Chart = Chart.WithLabels(pivotChartModel.ColumnDescriptors.Select(x => x.DisplayName).ToArray());
    }

    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
    }
}
