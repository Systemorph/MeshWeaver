using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.Arithmetics.Aggregation.Implementation;
using OpenSmc.Arithmetics.MapOver;
using OpenSmc.DataCubes.Operations;

namespace OpenSmc.DataCubes
{
    public class DataCube<T> : DataCubeBase<T>
    {
        static DataCube()
        {
            AggregationFunction.RegisterAggregationProviderBefore<IsValueTypeAggregationFunctionProvider>(
                new IsDataCubeAggregationFunctionProvider(),
                type => type.IsDataCube());

            MapOverFields.RegisterMapOverProviderBefore<IsSupportedValueTypeFunctionProvider>(
                new IsDataCubeMapOverFunctionProvider(),
                type => type.IsDataCube());

            SumFunction.RegisterSumProviderBefore<GenericSumFunctionProvider>(new IsDataCubeSumFunctionProvider(),
                t => t.IsDataCube());

        }

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