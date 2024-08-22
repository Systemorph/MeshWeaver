using MeshWeaver.Charting.Builders.ChartBuilders;
using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Charting.Pivot;

public record PivotRadarChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> :
    PivotArrayChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, RadarChart, RadarDataSet, RangeOptionsBuilder, RadarDataSetBuilder>,
    IPivotRadarChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    public PivotRadarChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        ChartBuilder = new RadarChart();
    }

    protected override PivotChartModel CreatePivotModel(PivotModel pivotModel)
    {
        return PivotChartModelBuilder.BuildFromPivotModel(pivotModel, ChartType.Radar);
    }

}
