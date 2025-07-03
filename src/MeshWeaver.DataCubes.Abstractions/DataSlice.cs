namespace MeshWeaver.DataCubes
{
    public interface IDataSlice
    {
        DimensionTuple Tuple { get; }
        object Data { get; }
        public IDataSlice Enrich(DimensionTuple additionalTuple);
    }

    public readonly struct DataSlice<T>(T data, DimensionTuple tuple) : IDataSlice
    {
        public T Data { get; } = data;
        public DimensionTuple Tuple { get; } = tuple;
        object IDataSlice.Data => Data!;

        private static IEnumerable<DataSlice<T>> Trivial(T data, string[] dimensions)
        {
            yield return new DataSlice<T>(data, new DimensionTuple(dimensions.Select(d => (d, (object)null))));
        }

        IDataSlice IDataSlice.Enrich(DimensionTuple additionalTuple)
        {
            if (additionalTuple.Count == 0)
                return this;
            return new DataSlice<T>(Data, Tuple.Enrich(additionalTuple));
        }
    }
}
