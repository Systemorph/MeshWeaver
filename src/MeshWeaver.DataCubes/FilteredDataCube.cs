namespace MeshWeaver.DataCubes
{
    public class FilteredDataCube<T> : DataCubeBase<T>
    {
        private readonly IDataCube<T> inner;
        private readonly Func<T, bool> filter;

        public FilteredDataCube(IDataCube<T> inner, Func<T, bool> filter)
        {
            this.inner = inner;
            this.filter = filter;
        }

        protected override IEnumerable<T> GetEnumerable()
        {
            if (filter == null)
                return inner;
            return inner.Where(filter);
        }


        public override IEnumerable<DataSlice<T>> GetSlices(params string[] dimensions)
        {
            return TuplesUtils<T>.GetDimensionTuples(dimensions, GetEnumerable());
        }

        public override IDataCube<T> Filter(params (string filter, object value)[] tuple)
        {
            var parsedFilter = TuplesUtils<T>.GetFilter(tuple);
            if (parsedFilter == null)
                return inner;
            return Filter(parsedFilter);
        }

        public override IDataCube<T> Filter(Func<T, bool> extraFilter)
        {
            bool Combined(T o) => filter(o) && extraFilter(o);
            return new FilteredDataCube<T>(inner, Combined);
        }
    }
}