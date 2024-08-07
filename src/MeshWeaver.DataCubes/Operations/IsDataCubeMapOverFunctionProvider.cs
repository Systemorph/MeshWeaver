using System.Reflection;
using MeshWeaver.Arithmetics;

namespace MeshWeaver.DataCubes.Operations
{
    public class IsDataCubeMapOverFunctionProvider : IMapOverFunctionProvider
    {
        private static readonly MethodInfo CreateMapOverDataCubeMethod = typeof(IsDataCubeMapOverFunctionProvider).GetMethod(nameof(CreateMapOverDataCube), BindingFlags.Static | BindingFlags.NonPublic);

        private static Func<double, IDataCube<TElement>, IDataCube<TElement>> CreateMapOverDataCube<TElement>(ArithmeticOperation method)
        {
            return (scalar, cube) => new MapOverDataCube<TElement>(cube, method, scalar);
        }

        public Delegate GetDelegate(Type type, ArithmeticOperation method)
        {
            return (Delegate)CreateMapOverDataCubeMethod.MakeGenericMethod(type.GetDataCubeElementType())
                                                        .Invoke(null, new object[] { method });
        }
    }
}