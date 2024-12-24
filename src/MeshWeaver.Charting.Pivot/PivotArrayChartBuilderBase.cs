using MeshWeaver.Charting.Models;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Charting.Pivot;

public abstract record PivotArrayChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, TDataSet> :
        PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, TDataSet>, IPivotArrayChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    where TDataSet : DataSetBase<TDataSet>, IDataSetWithPointStyle<TDataSet>, IDataSetWithOrder<TDataSet>,
    IDataSetWithFill<TDataSet>, IDataSetWithTension<TDataSet>, IDataSetWithPointRadiusAndRotation<TDataSet>
{
    protected PivotArrayChartBuilderBase(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder) : base(pivotBuilder)
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


    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
    }


}
