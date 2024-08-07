using MeshWeaver.Arithmetics.Aggregation;

namespace MeshWeaver.DataCubes.Operations
{
    public class PreAggregatedCube<T, TAggregated>
    {
        //private readonly HashSet<string> slicedDimensions;
        private readonly Dictionary<DimensionTuple, TAggregated> dataByTuples;


        private readonly Dictionary<string, ILookup<object, T>> dataByDimensionAndValue;
        private readonly Func<IEnumerable<T>, TAggregated> aggregationFunction;
        private readonly HashSet<DimensionTuple> tuples = new();

        ///// <summary>
        ///// Trivial grouping when only one tuple matches. E.g., none of the dimensions sliced by matches, and we only return grand total.
        ///// </summary>
        ///// <param name="data"></param>
        ///// <param name="tuple"></param>
        //public AggregateDataCube(IEnumerable<T> data, DimensionTuple tuple)
        //{
        //    dataByTuples = new Dictionary<DimensionTuple, T[]> {{tuple, data.ToArray()}};
        //}

        // ReSharper disable once StaticMemberInGenericType
        private static readonly object Default = new();

        public PreAggregatedCube(IEnumerable<DataSlice<T>> slices, Func<IEnumerable<T>, TAggregated> aggregationFunction = null)
        {
            //this.slicedDimensions = new HashSet<string>(slicedDimensions);
            this.aggregationFunction = aggregationFunction ?? (Func<IEnumerable<T>, TAggregated>)(object)AggregationFunction.GetAggregationFunction<T>();
            var slicesCollection = slices as ICollection<DataSlice<T>> ?? slices.ToArray();
            dataByDimensionAndValue = slicesCollection.SelectMany(s => s.Tuple.Select(t => new { t.Dimension, t.Value, s.Data }))
                                                      .GroupBy(x => x.Dimension)
                                                      .ToDictionary(x => x.Key, x =>
                                                                                    x.ToLookup(y => y.Value ?? Default,
                                                                                               z => z.Data));

            tuples.UnionWith(slicesCollection.Select(s => s.Tuple));
            dataByTuples = slicesCollection.GroupBy(x => x.Tuple)
                                           .ToDictionary(x => x.Key, x => this.aggregationFunction(x.Select(y => y.Data)));
        }

        public IEnumerable<DimensionTuple> Tuples => tuples;

        private readonly Dictionary<DimensionTuple, TAggregated> preAggregated = new();

        public TAggregated GetData(DimensionTuple tuple)
        {
            dataByTuples.TryGetValue(tuple, out var ret);
            return ret;
        }

        public TAggregated GetAggregatedData(DimensionTuple tuple)
        {
            // TODO: This is a very naive implementation. Should have more performant intersect and should work with pre-aggregated (2021/05/08, Roland Buergi)
            if (preAggregated.TryGetValue(tuple, out var ret))
                return ret;
            return preAggregated[tuple] = aggregationFunction(tuple.Select(GetDataForTuple).Aggregate((x, y) => x.Intersect(y)));
        }

        private IEnumerable<T> GetDataForTuple((string dimension, object value) tuple)
        {
            var (dimension, value) = tuple;
            if (!dataByDimensionAndValue.TryGetValue(dimension, out var inner))
                return Enumerable.Empty<T>();
            if (value == null)
                return inner.SelectMany(x => x);
            return inner[value];
        }
    }
}