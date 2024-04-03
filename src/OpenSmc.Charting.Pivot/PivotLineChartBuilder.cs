using OpenSmc.Charting.Builders.ChartBuilders;
using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Models;
using OpenSmc.Charting.Models;

namespace OpenSmc.Charting.Pivot;


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