using MeshWeaver.Charting.Builders;
using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Charting.Pivot;

record PivotLineChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    : PivotArrayChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, LineDataSet, LineDataSetBuilder>, IPivotLineChartBuilder
     where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    public PivotLineChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        Chart = Charts.Line([]);
    }

    protected override PivotChartModel CreatePivotModel(PivotModel pivotModel)
    {
        return PivotChartModelBuilder.BuildFromPivotModel(pivotModel, ChartType.Line);
    }

    public IPivotLineChartBuilder WithRangeOptionsBuilder(Func<ChartOptions, ChartOptions> func)
    {
        Chart = Chart.WithOptions(func);
        return this;
    }
}
