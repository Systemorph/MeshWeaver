using OpenSmc.Charting.Builders.ChartBuilders;
using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Models;

namespace OpenSmc.Charting.Pivot;

public record PivotRadarChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> :
    PivotArrayChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, RadarChartBuilder, RadarDataSet, RangeOptionsBuilder, RadarDataSetBuilder>,
    IPivotRadarChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    public PivotRadarChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        ChartBuilder = new RadarChartBuilder();
    }

    protected override PivotChartModel CreatePivotModel(PivotModel pivotModel)
    {
        return PivotChartModelBuilder.BuildFromPivotModel(pivotModel, ChartType.Radar);
    }

}