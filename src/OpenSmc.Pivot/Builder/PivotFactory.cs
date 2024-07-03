using OpenSmc.Collections;
using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.Pivot.Aggregations;

namespace OpenSmc.Pivot.Builder;

public static class PivotFactory
{
    public static PivotBuilder<T, T, T> Pivot<T>(this WorkspaceState state, IEnumerable<T> objects)
    {
        return new PivotBuilder<T, T, T>(state, objects).WithAggregation(a => a.Sum());
    }

    public static DataCubePivotBuilder<
        IDataCube<TElement>,
        TElement,
        TElement,
        TElement
    > ForDataCubes<TElement>(this WorkspaceState state, IEnumerable<IDataCube<TElement>> cubes)
    {
        var pivotBuilder = new DataCubePivotBuilder<
            IDataCube<TElement>,
            TElement,
            TElement,
            TElement
        >(state,cubes);
        pivotBuilder = pivotBuilder with
        {
            Aggregations = new Aggregations<DataSlice<TElement>, TElement>
            {
                Aggregation = slices =>
                    AggregationsExtensions.Aggregation(slices.Select(s => s.Data).ToArray()),
                AggregationOfAggregates = AggregationsExtensions.Aggregation
            }
        };

        return pivotBuilder;
    }

    public static DataCubePivotBuilder<
        IDataCube<TElement>,
        TElement,
        TElement,
        TElement
    > Pivot<TElement>(this WorkspaceState state, IDataCube<TElement> cube)
    {
        return state.ForDataCubes(cube.RepeatOnce());
    }
}
