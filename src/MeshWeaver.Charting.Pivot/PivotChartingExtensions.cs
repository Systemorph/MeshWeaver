using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Charting.Pivot;

public static class PivotChartingExtensions
{
    public static IPivotBarChartBuilder ToBarChartPivotBuilder<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
    {
        return new PivotBarChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>(
            pivotBuilder
        );
    }

    public static IPivotLineChartBuilder ToLineChart<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        where TPivotBuilder : PivotBuilderBase<
                T,
                TTransformed,
                TIntermediate,
                TAggregate,
                TPivotBuilder
            >
    {
        return new PivotLineChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>(
            pivotBuilder
        );
    }

    public static IPivotRadarChartBuilder ToRadarChart<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        where TPivotBuilder : PivotBuilderBase<
                T,
                TTransformed,
                TIntermediate,
                TAggregate,
                TPivotBuilder
            >
    {
        return new PivotRadarChartBuilder<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >(pivotBuilder);
    }

    public static IPivotWaterfallChartBuilder ToWaterfallChart<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        where TPivotBuilder : PivotBuilderBase<
                T,
                TTransformed,
                TIntermediate,
                TAggregate,
                TPivotBuilder
            >
    {
        return new PivotWaterfallChartBuilder<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >(pivotBuilder);
    }

    public static IPivotWaterfallChartBuilder ToHorizontalWaterfallChart<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        where TPivotBuilder : PivotBuilderBase<
                T,
                TTransformed,
                TIntermediate,
                TAggregate,
                TPivotBuilder
            >
    {
        return new PivotHorizontalWaterfallChartBuilder<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >(pivotBuilder);
    }
}
