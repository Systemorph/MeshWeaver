namespace OpenSmc.DataCubes
{
    public class DataCube<T> : DataCubeBase<T>
    {
        private readonly ICollection<T> data;


        public DataCube(ICollection<T> data)
        {
            this.data = data;
        }

        public override IEnumerable<DataSlice<T>> GetSlices(params string[] dimensions)
        {
            return TuplesUtils<T>.GetDimensionTuples(dimensions, data);
        }

        public override IDataCube<T> Filter(params (string filter, object value)[] tuple)
        {
            var parsedFilter = TuplesUtils<T>.GetFilter(tuple);
            return Filter(parsedFilter);
        }

        public override IDataCube<T> Filter(Func<T, bool> filter)
        {
            return new FilteredDataCube<T>(this, filter);
        }


        protected override IEnumerable<T> GetEnumerable()
        {
            return data;
        }
    }
}