using System.Reflection;
using Castle.DynamicProxy;
using MeshWeaver.Arithmetics;
using MeshWeaver.Arithmetics.MapOver;
using MeshWeaver.DataCubes;
using MeshWeaver.Reflection;
using MeshWeaver.Scopes.Operations;
using MeshWeaver.Scopes.Proxy;

namespace MeshWeaver.Scopes.DataCubes
{
    public class IsDataCubeScopeMapOverFunctionProvider : IMapOverFunctionProvider
    {
        private readonly IsScopeMapOverFunctionProvider.MapOverProxyGenerator proxyGenerator = new IsScopeMapOverFunctionProvider.MapOverProxyGenerator();
        
        public Delegate GetDelegate(Type type, ArithmeticOperation method) => (Delegate)CreateMapOverScopeMethod.MakeGenericMethod(type, type.GetDataCubeElementType()).Invoke(this, new object[] { method });

        private static readonly MethodInfo CreateMapOverScopeMethod = ReflectionHelper.GetMethodGeneric<IsDataCubeScopeMapOverFunctionProvider>(x => x.CreateMapOverScope<IDataCube<object>, object>(ArithmeticOperation.Plus));

        internal Func<double, TScope, TScope> CreateMapOverScope<TScope, TElement>(ArithmeticOperation method)
            where TScope : class, IDataCube<TElement>
        {
            return (scalar, scope) =>
                   {
                       var scopeInterceptor = proxyGenerator.GetMapOverInterceptor(scope, method, scalar);
                       var dataCubeScopeInterceptor = new DataCubeScopeMapOverInterceptor<TScope, TElement>(scope, method, scalar);
                       return proxyGenerator.CreateProxy<TScope>(dataCubeScopeInterceptor, scopeInterceptor);
                   };
        }
    }

    internal class DataCubeScopeMapOverInterceptor<TScope, TElement> : ScopeInterceptorBase
        where TScope : IDataCube<TElement>
    {
        private readonly IDataCube<TElement> scope;
        private readonly double scalar;
        private readonly Func<double, TElement, TElement> mapOverFunction;

        public DataCubeScopeMapOverInterceptor(TScope scope, ArithmeticOperation method, double scalar)
        {
            this.scope = scope;
            this.scalar = scalar;
            mapOverFunction = MapOverFields.GetMapOver<TElement>(method);
        }


        public override void Intercept(IInvocation invocation)
        {
            if (invocation.Method.DeclaringType.IsEnumerableInterface())
            {
                var enumerator = GetEnumerator();
                invocation.ReturnValue = enumerator;
                return;
            }

            if (invocation.Method.DeclaringType.IsDataCubeInterface())
            {
                switch (invocation.Method.Name)
                {
                    case nameof(IDataCube.GetSlices):
                        invocation.ReturnValue = GetSlices(invocation);
                        return;
                    case nameof(IDataCube.GetDimensionDescriptors):
                        invocation.ReturnValue = GetDimensionDescriptors(invocation);
                        return;
                    case nameof(IDataCube.Filter):
                        invocation.ReturnValue =
                            ((IFilterable)invocation.Proxy).Filter(((string, object)[])invocation.Arguments.First());
                        return;
                }
            }


            invocation.Proceed();
        }

        private object GetDimensionDescriptors(IInvocation invocation)
        {
            var isByRow = (bool)invocation.Arguments.First();
            var dimensions = (string[])invocation.Arguments.Skip(1).First(); 
            return scope.GetDimensionDescriptors(isByRow, dimensions);
        }

        private object GetSlices(IInvocation invocation)
        {
            return scope.GetSlices((string[])invocation.Arguments.First())
                        .Select(x => new DataSlice<TElement>(mapOverFunction(scalar, x.Data), x.Tuple));
        }

        private object GetEnumerator()
        {
            return scope.Select(e => mapOverFunction(scalar, e)).GetEnumerator();
        }
    }
}