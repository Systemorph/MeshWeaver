using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Charting.Pivot;

// todo: move to Charting.Layout project (07.08.2024, Alexander Kravets)
public static class PivotChartBuilderExtensions
{
    public static ChartControl ToBarChart<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IPivotBarChartBuilder, IPivotBarChartBuilder> builder = null
        )
        where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
    {
        var pivotBarChartBuilder = pivotBuilder.ToBarChartPivotBuilder();

        if (builder is not null)
        {
            pivotBarChartBuilder = builder(pivotBarChartBuilder);
        }

        var chartModel = pivotBarChartBuilder.Execute();

        return new(chartModel);
    }
}
