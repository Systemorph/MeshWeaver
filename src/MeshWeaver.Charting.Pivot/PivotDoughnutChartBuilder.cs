using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models.Segmented;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Charting.Pivot;

public record PivotDoughnutChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> :
    PivotChartBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>,
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

    protected override void AddDataSets(PivotChartModel pivotChartModel)
    {
        foreach (var row in pivotChartModel.Rows)
        {
            var dataset = new PieDataSet(row.DataByColumns.Select(x => x.Value).Cast<object>().ToArray())
                             .WithLabel(row.Descriptor.DisplayName);
            Chart = Chart.WithDataSet(dataset);
        }

        Chart = Chart.WithLabels(pivotChartModel.ColumnDescriptors.Select(x => x.DisplayName).ToArray());
    }

    protected override void AddOptions(PivotChartModel pivotChartModel)
    {
    }
}
