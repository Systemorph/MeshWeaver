using MeshWeaver.Arithmetics.Aggregation;
using MeshWeaver.Arithmetics.MapOver;
using MeshWeaver.DataCubes;
using MeshWeaver.DataCubes.Operations;
using MeshWeaver.Scopes.Proxy;

namespace MeshWeaver.Scopes.DataCubes
{
    public class DataCubeScopeInterceptorFactory : IScopeInterceptorFactory
    {
        static DataCubeScopeInterceptorFactory()
        {
            AggregationFunction.RegisterAggregationProviderBefore<IsDataCubeAggregationFunctionProvider>(
                new IsDataCubeScopeAggregationFunctionProvider(),
                type => type.IsScope() && type.IsDataCube());

            MapOverFields.RegisterMapOverProviderBefore<IsDataCubeMapOverFunctionProvider>(new IsDataCubeScopeMapOverFunctionProvider(),
                type => type.IsDataCube());

        }

        public IEnumerable<IScopeInterceptor> GetInterceptors(Type scopeType, IInternalScopeFactory scopeFactory)
        {
            var dataCubeElement = scopeType.GetDataCubeElementType();
            var identityType = scopeType.GetIdentityType();
            if (dataCubeElement != null)
            {
                var interceptorType = typeof(DataCubeScopeInterceptor<,,>).MakeGenericType(scopeType, dataCubeElement, identityType);
                yield return (IScopeInterceptor)Activator.CreateInstance(interceptorType, scopeFactory.ScopeRegistry);
            }
        }
    }
}