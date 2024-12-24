using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models.Segmented;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Charting.Pivot;

public record PivotDoughnutChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> :
    PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder, DoughnutDataSet>,
    IPivotChartBuilder
    where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
{
    public PivotDoughnutChartBuilder(PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        : base(pivotBuilder)
    {
        Chart = new();
    }

    protected override PivotChartModel CreatePivotModel(PivotModel pivotModel)
    {
        return PivotChartModelBuilder.BuildFromPivotModel(pivotModel, ChartType.Doughnut);
    }


    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
    }
}
