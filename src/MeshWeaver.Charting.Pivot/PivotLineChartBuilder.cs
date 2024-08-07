using MeshWeaver.Charting.Builders.ChartBuilders;
using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Pivot;


record PivotLineChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    : PivotArrayChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, LineChartBuilder, LineDataSet, LineOptionsBuilder, LineDataSetBuilder>, IPivotLineChartBuilder
     where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    public PivotLineChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        ChartBuilder = new LineChartBuilder();
    }


    protected override PivotChartModel CreatePivotModel(PivotModel pivotModel)
    {
        return PivotChartModelBuilder.BuildFromPivotModel(pivotModel, ChartType.Line);
    }

    public IPivotLineChartBuilder WithRangeOptionsBuilder(Func<LineOptionsBuilder, LineOptionsBuilder> func)
    {
        ChartBuilder = ChartBuilder.WithOptions(func);
        return this;
    }
}