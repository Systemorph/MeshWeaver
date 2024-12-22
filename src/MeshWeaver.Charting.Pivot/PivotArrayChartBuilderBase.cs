using MeshWeaver.Charting.Models;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Charting.Pivot;

public abstract record PivotArrayChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, TDataSet> :
        PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>, IPivotArrayChartBuilder
        where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
        where TDataSet : DataSet<TDataSet>, IDataSetWithPointStyle<TDataSet>, IDataSetWithOrder<TDataSet>, IDataSetWithFill<TDataSet>, IDataSetWithTension<TDataSet>, IDataSetWithPointRadiusAndRotation<TDataSet>
{
    private readonly Func<IReadOnlyCollection<object>, TDataSet> dataSetFactory;

    protected PivotArrayChartBuilderBase(
        PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IReadOnlyCollection<object>, TDataSet> dataSetFactory)
        : base(pivotBuilder)
    {
        this.dataSetFactory = dataSetFactory;
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
            var dataSet = dataSetFactory.Invoke(
                    row.DataByColumns.Select(x => x.Value).Cast<object>().ToArray()
                    ).WithLabel(row.Descriptor.DisplayName);
            dataSet = row.Filled ? dataSet.WithArea() : dataSet.WithoutFill();
            dataSet = row.SmoothingCoefficient != null ?
                dataSet.Smoothed((double)row.SmoothingCoefficient) :
                dataSet;
            Chart = Chart.WithDataSet(dataSet);
        }


        Chart = Chart.WithLabels(pivotChartModel.ColumnDescriptors.Select(x => x.DisplayName).ToArray());
    }

    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
    }
}
