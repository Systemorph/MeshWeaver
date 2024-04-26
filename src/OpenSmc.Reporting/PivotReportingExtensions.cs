using OpenSmc.DataCubes;
using OpenSmc.GridModel;
using OpenSmc.Pivot.Builder;
using OpenSmc.Reporting.Builder;

namespace OpenSmc.Reporting;

public static class PivotReportingExtensions
{
    public static ReportBuilder<T, TIntermediate, TAggregate> ToTable<T, TIntermediate, TAggregate>(
        this PivotBuilder<T, TIntermediate, TAggregate> pivotBuilder
    )
    {
        return new ReportBuilder<T, TIntermediate, TAggregate>(pivotBuilder);
    }

    public static DataCubeReportBuilder<
        IDataCube<TElement>,
        TElement,
        TIntermediate,
        TAggregate
    > ToTable<TElement, TIntermediate, TAggregate>(
        this DataCubePivotBuilder<
            IDataCube<TElement>,
            TElement,
            TIntermediate,
            TAggregate
        > pivotBuilder,
        Func<GridOptions, GridOptions> gridOptionsPostProcessor = null
    )
    {
        return new DataCubeReportBuilder<IDataCube<TElement>, TElement, TIntermediate, TAggregate>(
            pivotBuilder,
            gridOptionsPostProcessor
        );
    }
}
