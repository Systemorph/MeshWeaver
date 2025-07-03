using System.Reflection;
using MeshWeaver.Arithmetics.Aggregation.Implementation;

namespace MeshWeaver.DataCubes.Operations
{
    public class IsDataCubeAggregationFunctionProvider : IAggregationFunctionProvider
    {
        public Delegate GetDelegate(Type elementType) => (Delegate)CreateAggregateDataCubeMethod!.MakeGenericMethod(elementType, elementType.GetDataCubeElementType()!).Invoke(null, null)!;

        private static readonly MethodInfo? CreateAggregateDataCubeMethod = typeof(IsDataCubeAggregationFunctionProvider).GetMethod(nameof(CreateAggregateDataCube), BindingFlags.Static | BindingFlags.NonPublic);

        private static Func<IEnumerable<TCube>, TCube> CreateAggregateDataCube<TCube, TElement>()
            where TCube : class, IDataCube<TElement>
        {
            Func<IEnumerable<TCube>, TCube> ret = cubes => (TCube)cubes.Aggregate()!;
            return ret;
        }
    }
}