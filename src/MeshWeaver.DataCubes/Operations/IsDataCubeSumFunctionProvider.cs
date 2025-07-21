﻿using System.Reflection;
using MeshWeaver.Arithmetics.Aggregation.Implementation;
using MeshWeaver.Reflection;
using MeshWeaver.Utils;

namespace MeshWeaver.DataCubes.Operations
{
    public class IsDataCubeSumFunctionProvider : ISumFunctionProvider
    {
        public Delegate CreateSumFunctionWithResult(Type type)
        {
            var elementType = type.GetDataCubeElementType();
            return SumMethod.MakeGenericMethod(type, elementType!).CreateDelegate(typeof(Func<,,>).MakeGenericType(type, type, typeof(IDataCube<>).MakeGenericType(elementType!)));
        }

        private static readonly MethodInfo SumMethod = ReflectionHelper.GetStaticMethodGeneric(() => Sum<IDataCube<object>, object>(null!, null!));

        public static IDataCube<T> Sum<TCube, T>(TCube x, TCube y)
            where TCube : IDataCube<T>
            => ((IDataCube<T>)x).RepeatOnce().Concat(((IDataCube<T>)y).RepeatOnce()).Aggregate()!;


        public Delegate CreateSumDelegate(Type type) => CreateSumFunctionWithResult(type);
    }
}
