using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models.Segmented;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Charting.Pivot;

public record PivotPieChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> :
    PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, PieDataSet>,
    IPivotChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    public PivotPieChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        Chart = new();
    }

    protected override PivotChartModel CreatePivotModel(PivotModel pivotModel)
    {
        return PivotChartModelBuilder.BuildFromPivotModel(pivotModel, ChartType.Pie);
    }



    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
    }
}
