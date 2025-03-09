using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Utils;

namespace MeshWeaver.Pivot.Builder;

public static class PivotFactory
{
    public static PivotBuilder<T, T, T> Pivot<T>(this IWorkspace workspace, IEnumerable<T> objects)
    {
        return new PivotBuilder<T, T, T>(workspace, objects).WithAggregation(a => a.Sum());
    }

    public static DataCubePivotBuilder<
        IDataCube<TElement>,
        TElement,
        TElement,
        TElement
    > ForDataCubes<TElement>(this IWorkspace workspace, IEnumerable<IDataCube<TElement>> cubes)
    {
        var pivotBuilder = new DataCubePivotBuilder<
            IDataCube<TElement>,
            TElement,
            TElement,
            TElement
        >(workspace, cubes);
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
    > Pivot<TElement>(this IWorkspace state, IDataCube<TElement> cube)
    {
        return state.ForDataCubes(cube.RepeatOnce());
    }
}
